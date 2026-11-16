"""Convert an RVC voice model (.pth, v1 or v2, 32/40/48 kHz) to ONNX for MicFX.

    python convert_rvc.py MODEL.pth OUT.onnx
    python convert_rvc.py --untrained v2 40000 OUT.onnx    # random weights, for tests

Only convert voices you have the right to use.

The model's network code comes from RVC (Retrieval-based-Voice-Conversion-WebUI,
MIT, Copyright (c) 2023 liujing04, 源文雨, Ftps). It is downloaded once from a
pinned commit, each file checked against its SHA-256, into
~/.cache/micfx-rvc/<commit>/.

The exported graph is RVC's real-time inference: the text encoder runs over
all frames, the flow over the last 24 frames before `skip_head`, the decoder
only over the `return_length` frames from `skip_head` on.

    phone         float32 [1, T, 256|768]   content features, 10 ms frames
    phone_lengths int64   [1]               T
    pitch         int64   [1, T]            coarse pitch 1..255     (pitch models only)
    pitchf        float32 [1, T]            f0 in Hz                (pitch models only)
    ds            int64   [1]               speaker id (0)
    rnd           float32 [1, 192, T]       N(0, 1) * 0.66666
    skip_head     int64   [1]
    return_length int64   [1]
    audio         float32 [1, 1, return_length * sr / 100]

Metadata key "metadata": JSON with samplingRate, f0, embChannels, version,
format = "micfx-rvc-1" and frames (the frame count T the graph was traced
with; other lengths are checked below and the result printed).

Needs torch, numpy, scipy, onnx and onnxruntime.
"""

import hashlib
import json
import os
import sys
import urllib.request

import numpy as np
import onnx
import onnxruntime as ort
import torch

RVC_REPO = "https://raw.githubusercontent.com/RVC-Project/Retrieval-based-Voice-Conversion-WebUI"
RVC_COMMIT = "81eed5e8f68b6bed1789f682fe78cdd324495afc"
RVC_FILES = {
    "attentions.py": "8820e6047353d99344fc73fbb6c69d85e4f61066fc498982cc895e9577d4519f",
    "commons.py": "132a907eff444907861e63684c04c88ae631d03707470858f7c53f857b0cc5c8",
    "models.py": "769615b8dbed4775c8083881663a79c0e5ec0b662dd5f0c0a1bc3ccdd9827e99",
    "modules.py": "b1aa77b0921468a61492fa89ed1f1212cd22fee3caa551befe44d8ab359fd2f1",
    "transforms.py": "fb0d3320adf32ca0eaa44dd56e8922927f77ec3dd45c775e10c3da1704072c6a",
}
TRACE_FRAMES = 128
UNTRAINED_CONFIGS = {
    # spec, segment, inter, hidden, filter, heads, layers, kernel, dropout, resblock,
    # resblock kernels, dilations, upsample rates, initial channel, upsample kernels, speakers, gin, sr
    32000: [513, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 8, 2, 2], 512, [20, 16, 4, 4], 109, 256, 32000],
    40000: [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [10, 10, 2, 2], 512, [16, 16, 4, 4], 109, 256, 40000],
    48000: [1025, 32, 192, 192, 768, 2, 6, 3, 0, "1", [3, 7, 11], [[1, 3, 5]] * 3, [12, 10, 2, 2], 512, [24, 20, 4, 4], 109, 256, 48000],
}


def rvc_modules():
    root = os.path.join(os.path.expanduser("~"), ".cache", "micfx-rvc", RVC_COMMIT)
    folder = os.path.join(root, "infer", "module")
    os.makedirs(folder, exist_ok=True)
    for name in ("infer", os.path.join("infer", "module")):
        open(os.path.join(root, name, "__init__.py"), "a").close()
    for name, digest in RVC_FILES.items():
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            url = f"{RVC_REPO}/{RVC_COMMIT}/infer/module/{name}"
            with urllib.request.urlopen(url) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != digest:
                raise SystemExit(f"{url}: unexpected content")
            with open(path, "wb") as f:
                f.write(data)
        elif hashlib.sha256(open(path, "rb").read()).hexdigest() != digest:
            raise SystemExit(f"{path}: unexpected content; delete it to download again")
    sys.path.insert(0, root)
    from infer.module import models
    return models


class RealtimeSynth(torch.nn.Module):
    def __init__(self, net, uses_pitch):
        super().__init__()
        self.net = net
        self.uses_pitch = uses_pitch

    def run(self, phone, phone_lengths, pitch, pitchf, ds, rnd, skip_head, return_length):
        net = self.net
        g = net.emb_g(ds).unsqueeze(-1)
        m_p, logs_p, x_mask = net.enc_p(phone, pitch, phone_lengths)
        frames = phone.shape[1]
        flow_head = torch.clamp(skip_head - 24, min=0)
        keep = torch.arange(flow_head[0], frames)
        m_p, logs_p, x_mask, rnd = (t.index_select(2, keep) for t in (m_p, logs_p, x_mask, rnd))
        z_p = (m_p + torch.exp(logs_p) * rnd) * x_mask
        z = net.flow(z_p, x_mask, g=g, reverse=True)
        dec_head = skip_head - flow_head
        ret = torch.arange(dec_head[0], dec_head[0] + return_length[0])
        z, x_mask = z.index_select(2, ret), x_mask.index_select(2, ret)
        if self.uses_pitch:
            nsff0 = pitchf.index_select(1, torch.arange(skip_head[0], skip_head[0] + return_length[0]))
            return net.dec(z * x_mask, nsff0, g=g)
        return net.dec(z * x_mask, g=g)


class WithPitch(RealtimeSynth):
    def forward(self, phone, phone_lengths, pitch, pitchf, ds, rnd, skip_head, return_length):
        return self.run(phone, phone_lengths, pitch, pitchf, ds, rnd, skip_head, return_length)


class WithoutPitch(RealtimeSynth):
    def forward(self, phone, phone_lengths, ds, rnd, skip_head, return_length):
        return self.run(phone, phone_lengths, None, None, ds, rnd, skip_head, return_length)


def build(models, config, version, uses_pitch, state=None):
    dim = 768 if version == "v2" else 256
    cls = {
        (256, True): models.SynthesizerTrnMs256NSFsid,
        (768, True): models.SynthesizerTrnMs768NSFsid,
        (256, False): models.SynthesizerTrnMs256NSFsid_nono,
        (768, False): models.SynthesizerTrnMs768NSFsid_nono,
    }[(dim, uses_pitch)]
    net = cls(*config, is_half=False)
    del net.enc_q
    if state is not None:
        missing, unexpected = net.load_state_dict(state, strict=False)
        missing = [k for k in missing if not k.startswith("enc_q.")]
        if missing:
            raise SystemExit(f"the model file does not fit RVC {version}: missing {missing[:5]}")
    net.eval()
    net.remove_weight_norm()
    return (WithPitch if uses_pitch else WithoutPitch)(net, uses_pitch).eval(), dim


def inputs(dim, frames, uses_pitch, skip, ret, seed=0):
    rng = np.random.default_rng(seed)
    feeds = {
        "phone": rng.standard_normal((1, frames, dim)).astype(np.float32),
        "phone_lengths": np.array([frames], dtype=np.int64),
    }
    if uses_pitch:
        f0 = (150 * 2 ** (0.3 * np.sin(np.arange(frames) / 9))).astype(np.float32)[None, :]
        f0[:, frames // 3: frames // 3 + 6] = 0
        mel = 1127 * np.log(1 + f0 / 700)
        lo, hi = 1127 * np.log(1 + 50 / 700), 1127 * np.log(1 + 1100 / 700)
        coarse = np.where(mel > 0, (mel - lo) * 254 / (hi - lo) + 1, mel)
        feeds["pitch"] = np.rint(np.clip(coarse, 1, 255)).astype(np.int64)
        feeds["pitchf"] = f0
    feeds["ds"] = np.array([0], dtype=np.int64)
    feeds["rnd"] = (rng.standard_normal((1, 192, frames)) * 0.66666).astype(np.float32)
    feeds["skip_head"] = np.array([skip], dtype=np.int64)
    feeds["return_length"] = np.array([ret], dtype=np.int64)
    return feeds


def main(argv):
    torch.manual_seed(0)
    models = rvc_modules()
    if argv[0] == "--untrained":
        version, sr, out_path = argv[1], int(argv[2]), argv[3]
        config, uses_pitch, state = list(UNTRAINED_CONFIGS[sr]), True, None
    else:
        model_path, out_path = argv[0], argv[1]
        cpt = torch.load(model_path, map_location="cpu", weights_only=True)
        if "weight" not in cpt or "config" not in cpt:
            raise SystemExit("not an RVC inference model (expected 'weight' and 'config'; "
                             "training checkpoints G_*.pth must be exported from RVC first)")
        config = list(cpt["config"])
        config[-3] = cpt["weight"]["emb_g.weight"].shape[0]
        version, uses_pitch, state = cpt.get("version", "v1"), bool(cpt.get("f0", 1)), cpt["weight"]
        if isinstance(config[-1], str):
            config[-1] = {"32k": 32000, "40k": 40000, "48k": 48000}[config[-1]]
    model, dim = build(models, config, version, uses_pitch, state)
    sr = int(config[-1])
    names = ["phone", "phone_lengths"] + (["pitch", "pitchf"] if uses_pitch else []) + ["ds", "rnd", "skip_head", "return_length"]

    # The NSF source adds Gaussian noise; make it zero for the parity runs only.
    sine = model.net.dec.m_source.l_sin_gen
    original_randn_like = torch.randn_like

    def export(path, deterministic):
        torch.randn_like = (lambda x, *a, **k: torch.zeros_like(x)) if deterministic else original_randn_like
        try:
            feeds = inputs(dim, TRACE_FRAMES, uses_pitch, TRACE_FRAMES - 16, 16)
            args = tuple(torch.from_numpy(feeds[n]) for n in names)
            with torch.no_grad():
                torch.onnx.export(
                    model, args, path, input_names=names, output_names=["audio"],
                    dynamic_axes={"phone": {1: "frames"}, "pitch": {1: "frames"}, "pitchf": {1: "frames"},
                                  "rnd": {2: "frames"}, "audio": {2: "samples"}},
                    opset_version=17, dynamo=False)
        finally:
            torch.randn_like = original_randn_like

    export(out_path, deterministic=False)
    meta = {"samplingRate": sr, "f0": int(uses_pitch), "embChannels": dim, "version": version,
            "format": "micfx-rvc-1", "frames": TRACE_FRAMES}
    m = onnx.load(out_path)
    m.metadata_props.add(key="metadata", value=json.dumps(meta))
    onnx.save(m, out_path)

    # Parity: the same graph without noise, against PyTorch without noise.
    check_path = out_path + ".check.onnx"
    export(check_path, deterministic=True)
    session = ort.InferenceSession(check_path, providers=["CPUExecutionProvider"])
    report = []
    torch.randn_like = lambda x, *a, **k: torch.zeros_like(x)
    try:
        for frames, ret in [(TRACE_FRAMES, 16), (TRACE_FRAMES, 21), (100, 16), (160, 31)]:
            feeds = inputs(dim, frames, uses_pitch, frames - ret, ret, seed=frames + ret)
            with torch.no_grad():
                ref = model(*(torch.from_numpy(feeds[n]) for n in names)).numpy()
            try:
                got = session.run(["audio"], {n: feeds[n] for n in names})[0]
                ok = got.shape == ref.shape == (1, 1, ret * sr // 100)
                diff = float(np.abs(got - ref).max()) if got.shape == ref.shape else float("nan")
                report.append(f"T={frames} return={ret}: {'shape ok' if ok else f'shape {got.shape} vs {ref.shape}'}, max difference {diff:.2e}")
            except Exception as e:  # noqa: BLE001 - reported, not hidden
                report.append(f"T={frames} return={ret}: fails ({str(e).splitlines()[0][:120]})")
    finally:
        torch.randn_like = original_randn_like
        os.remove(check_path)

    data = open(out_path, "rb").read()
    print(f"file      {out_path}")
    print(f"model     RVC {version}, {sr} Hz, {'with' if uses_pitch else 'without'} pitch, {dim} features")
    print(f"size      {len(data)}")
    print(f"sha256    {hashlib.sha256(data).hexdigest()}")
    for line in report:
        print(f"parity    {line}")


if __name__ == "__main__":
    if len(sys.argv) not in (3, 5):
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1:])
