"""Write the tiny ONNX models in tests/MicFX.Tests/Fixtures.

They have the inputs and outputs of the real models but compute something
simple and checkable, so the C# wrappers can be tested with ONNX Runtime
without downloading anything:

  gen-micfx.onnx        convert_rvc.py format, 40 kHz, 768 features, 64 frames:
                        frame f of the output = pitchf[skip+f] + phone[skip+f, 0]
  gen-wokada-half.onnx  w-okada format, float16 features, 32 kHz, 256 features:
                        every frame = pitchf + feats[., 0]
  gen-webui.onnx        old WebUI format, 48 kHz, 256 features, no metadata:
                        every frame = pitchf + phone[., 0] + mean(rnd) * 0
  contentvec-tiny.onnx  frame f of both outputs = mean of samples [320 f, 320 f + 400)
  fcpe-tiny.onnx        frame i = 100 + 1000 * mean of samples [160 i, 160 i + 160)

Usage: python make_test_models.py OUT_DIR   (needs onnx)
"""

import json
import os
import sys

import numpy as np
import onnx
from onnx import TensorProto, helper, numpy_helper

OPSET = [helper.make_opsetid("", 17)]


def const(name, value, dtype=np.int64):
    return numpy_helper.from_array(np.array(value, dtype=dtype), name)


def save(graph, path, meta=None):
    model = helper.make_model(graph, opset_imports=OPSET, producer_name="micfx-tests")
    model.ir_version = 8
    if meta is not None:
        model.metadata_props.add(key="metadata", value=json.dumps(meta))
    onnx.checker.check_model(model)
    onnx.save(model, path)


def frame_values_to_audio(nodes, values, per_frame, out="audio"):
    """values [1, F] -> audio [1, 1, F * per_frame], each frame held constant."""
    nodes += [
        helper.make_node("Unsqueeze", [values, "ax2"], ["v3"]),
        helper.make_node("Expand", ["v3", "expand_shape"], ["held"]),
        helper.make_node("Reshape", ["held", "audio_shape"], [out]),
    ]
    return [const("ax2", [2]), const("expand_shape", [1, 1, per_frame]), const("audio_shape", [1, 1, -1])]


def generator_micfx(path):
    dim, per = 768, 400
    inputs = [
        helper.make_tensor_value_info("phone", TensorProto.FLOAT, [1, "T", dim]),
        helper.make_tensor_value_info("phone_lengths", TensorProto.INT64, [1]),
        helper.make_tensor_value_info("pitch", TensorProto.INT64, [1, "T"]),
        helper.make_tensor_value_info("pitchf", TensorProto.FLOAT, [1, "T"]),
        helper.make_tensor_value_info("ds", TensorProto.INT64, [1]),
        helper.make_tensor_value_info("rnd", TensorProto.FLOAT, [1, 192, "T"]),
        helper.make_tensor_value_info("skip_head", TensorProto.INT64, [1]),
        helper.make_tensor_value_info("return_length", TensorProto.INT64, [1]),
    ]
    nodes = [
        helper.make_node("Add", ["skip_head", "return_length"], ["end"]),
        helper.make_node("Slice", ["pitchf", "skip_head", "end", "ax1"], ["f0_ret"]),
        helper.make_node("Slice", ["phone", "zero", "one", "ax2"], ["phone0"]),           # [1, T, 1]
        helper.make_node("Squeeze", ["phone0", "ax2"], ["phone0s"]),                       # [1, T]
        helper.make_node("Slice", ["phone0s", "skip_head", "end", "ax1"], ["phone_ret"]),
        helper.make_node("Add", ["f0_ret", "phone_ret"], ["values"]),
    ]
    inits = [const("ax1", [1]), const("zero", [0]), const("one", [1])]
    inits += frame_values_to_audio(nodes, "values", per)
    out = [helper.make_tensor_value_info("audio", TensorProto.FLOAT, [1, 1, "N"])]
    graph = helper.make_graph(nodes, "gen_micfx", inputs, out, inits)
    save(graph, path, {"samplingRate": 40000, "f0": 1, "embChannels": dim, "version": "v2",
                       "format": "micfx-rvc-1", "frames": 64})


def generator_window(path, half, dim, per, meta):
    feat_name = "feats" if half is not None else "phone"
    feat_type = TensorProto.FLOAT16 if half else TensorProto.FLOAT
    inputs = [helper.make_tensor_value_info(feat_name, feat_type, [1, "T", dim])]
    if half is not None:
        inputs += [
            helper.make_tensor_value_info("p_len", TensorProto.INT64, [1]),
            helper.make_tensor_value_info("pitch", TensorProto.INT64, [1, "T"]),
            helper.make_tensor_value_info("pitchf", TensorProto.FLOAT, [1, "T"]),
            helper.make_tensor_value_info("sid", TensorProto.INT64, [1]),
        ]
    else:
        inputs += [
            helper.make_tensor_value_info("phone_lengths", TensorProto.INT64, [1]),
            helper.make_tensor_value_info("pitch", TensorProto.INT64, [1, "T"]),
            helper.make_tensor_value_info("pitchf", TensorProto.FLOAT, [1, "T"]),
            helper.make_tensor_value_info("ds", TensorProto.INT64, [1]),
            helper.make_tensor_value_info("rnd", TensorProto.FLOAT, [1, 192, "T"]),
        ]
    nodes = []
    src = feat_name
    if half:
        nodes.append(helper.make_node("Cast", [feat_name], ["feats32"], to=TensorProto.FLOAT))
        src = "feats32"
    nodes += [
        helper.make_node("Slice", [src, "zero", "one", "ax2"], ["f0col"]),
        helper.make_node("Squeeze", ["f0col", "ax2"], ["feat0"]),
        helper.make_node("Add", ["pitchf", "feat0"], ["values"]),
    ]
    inits = [const("zero", [0]), const("one", [1])]
    inits += frame_values_to_audio(nodes, "values", per)
    out = [helper.make_tensor_value_info("audio", TensorProto.FLOAT, [1, 1, "N"])]
    graph = helper.make_graph(nodes, "gen_window", inputs, out, inits)
    save(graph, path, meta)


def content_encoder(path):
    inputs = [helper.make_tensor_value_info("audio", TensorProto.FLOAT, [1, "L"])]
    nodes = [
        helper.make_node("Unsqueeze", ["audio", "ax1"], ["a3"]),                          # [1, 1, L]
        helper.make_node("Conv", ["a3", "w"], ["m"], strides=[320]),                       # [1, 1, F]
        helper.make_node("Transpose", ["m"], ["mt"], perm=[0, 2, 1]),                      # [1, F, 1]
        helper.make_node("Expand", ["mt", "shape768"], ["feats768"]),
        helper.make_node("Expand", ["mt", "shape256"], ["feats256"]),
    ]
    inits = [const("ax1", [1]), numpy_helper.from_array(np.full((1, 1, 400), 1 / 400, np.float32), "w"),
             const("shape768", [1, 1, 768]), const("shape256", [1, 1, 256])]
    outs = [helper.make_tensor_value_info("feats768", TensorProto.FLOAT, [1, "F", 768]),
            helper.make_tensor_value_info("feats256", TensorProto.FLOAT, [1, "F", 256])]
    save(helper.make_graph(nodes, "contentvec_tiny", inputs, outs, inits), path)


def pitch_extractor(path):
    inputs = [helper.make_tensor_value_info("audio", TensorProto.FLOAT, [1, "L"])]
    nodes = [
        helper.make_node("Pad", ["audio", "pads"], ["padded"]),                            # one hop of zeros at the end
        helper.make_node("Unsqueeze", ["padded", "ax1"], ["a3"]),
        helper.make_node("Conv", ["a3", "w"], ["m"], strides=[160]),                       # [1, 1, L/160 + 1]
        helper.make_node("Squeeze", ["m", "ax1"], ["ms"]),
        helper.make_node("Add", ["ms", "hundred"], ["f0"]),
    ]
    inits = [const("pads", [0, 0, 0, 160]), const("ax1", [1]),
             numpy_helper.from_array(np.full((1, 1, 160), 1000 / 160, np.float32), "w"),
             numpy_helper.from_array(np.array(100, np.float32), "hundred")]
    outs = [helper.make_tensor_value_info("f0", TensorProto.FLOAT, [1, "N"])]
    save(helper.make_graph(nodes, "fcpe_tiny", inputs, outs, inits), path)


def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)
    generator_micfx(os.path.join(out_dir, "gen-micfx.onnx"))
    generator_window(os.path.join(out_dir, "gen-wokada-half.onnx"), True, 256, 320,
                     {"samplingRate": 32000, "f0": 1, "embChannels": 256, "version": "v1"})
    generator_window(os.path.join(out_dir, "gen-webui.onnx"), None, 256, 480, None)
    content_encoder(os.path.join(out_dir, "contentvec-tiny.onnx"))
    pitch_extractor(os.path.join(out_dir, "fcpe-tiny.onnx"))
    for name in sorted(os.listdir(out_dir)):
        print(f"{name:24s} {os.path.getsize(os.path.join(out_dir, name))} bytes")


if __name__ == "__main__":
    main(sys.argv[1])
