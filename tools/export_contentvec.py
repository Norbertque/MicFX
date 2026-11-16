"""Export the ContentVec content encoder (MIT) to ONNX for MicFX.

Source: the Transformers conversion of ContentVec at
huggingface.co/lengyue233/content-vec-best (MIT), pinned to a revision below.
RVC calls the same network through fairseq: extract_features(output_layer=12)
for v2 models, and final_proj(extract_features(output_layer=9)) for v1.

    input  audio      float32 [1, samples]        16 kHz mono, not normalised
    output feats768   float32 [1, frames, 768]    layer 12 (RVC v2)
    output feats256   float32 [1, frames, 256]    final_proj of layer 9 (RVC v1)
    frames = (samples - 400) // 320 + 1

Usage: python export_contentvec.py OUT.onnx
Needs torch, transformers, huggingface_hub, onnx and onnxruntime. Prints the
file's SHA-256 and the largest difference to PyTorch.
"""

import hashlib
import math
import sys

import numpy as np
import onnxruntime as ort
import torch
from huggingface_hub import hf_hub_download
from transformers import HubertConfig, HubertModel

REPO = "lengyue233/content-vec-best"
REVISION = "ab04aa7067b99ee05cc82499bc64916b980a1967"
WEIGHTS_SHA256 = "d8dd400e054ddf4e6be75dab5a2549db748cc99e756a097c496c099f65a4854e"


class HubertModelWithFinalProj(HubertModel):
    def __init__(self, config):
        super().__init__(config)
        self.final_proj = torch.nn.Linear(config.hidden_size, config.classifier_proj_size)


class ContentVec(torch.nn.Module):
    def __init__(self, hubert):
        super().__init__()
        self.hubert = hubert

    def forward(self, audio):
        out = self.hubert(audio, output_hidden_states=True)
        layer9 = out.hidden_states[9]
        layer12 = out.hidden_states[12]
        return layer12, self.hubert.final_proj(layer9)


def load():
    config_path = hf_hub_download(REPO, "config.json", revision=REVISION)
    weights_path = hf_hub_download(REPO, "pytorch_model.bin", revision=REVISION)
    digest = hashlib.sha256(open(weights_path, "rb").read()).hexdigest()
    if digest != WEIGHTS_SHA256:
        raise SystemExit(f"unexpected weights: sha256 {digest}")
    config = HubertConfig.from_pretrained(config_path)
    model = HubertModelWithFinalProj(config)
    state = torch.load(weights_path, map_location="cpu", weights_only=True)
    missing, unexpected = model.load_state_dict(state, strict=False)
    missing = [k for k in missing if "masked_spec_embed" not in k]
    if missing or unexpected:
        raise SystemExit(f"weights do not fit: missing {missing}, unexpected {unexpected}")
    return ContentVec(model.eval()).eval()


def test_signal(seconds, seed):
    rng = np.random.default_rng(seed)
    n = int(16000 * seconds)
    t = np.arange(n) / 16000
    f0 = 120 * 2 ** (0.5 * np.sin(2 * math.pi * 0.7 * t))
    phase = 2 * math.pi * np.cumsum(f0) / 16000
    x = 0.1 * sum(np.sin(k * phase) / k for k in range(1, 30)) + 0.01 * rng.standard_normal(n)
    return x.astype(np.float32)[None, :]


def main(out_path):
    model = load()
    example = torch.from_numpy(test_signal(1.0, 0))
    with torch.no_grad():
        torch.onnx.export(
            model, (example,), out_path,
            input_names=["audio"], output_names=["feats768", "feats256"],
            dynamic_axes={"audio": {1: "samples"}, "feats768": {1: "frames"}, "feats256": {1: "frames"}},
            opset_version=17, dynamo=False)

    session = ort.InferenceSession(out_path, providers=["CPUExecutionProvider"])
    worst = 0.0
    for seconds, seed in [(0.6, 1), (1.4, 2)]:
        x = test_signal(seconds, seed)
        with torch.no_grad():
            ref768, ref256 = (t.numpy() for t in model(torch.from_numpy(x)))
        got768, got256 = session.run(["feats768", "feats256"], {"audio": x})
        frames = (x.shape[1] - 400) // 320 + 1
        assert got768.shape == ref768.shape == (1, frames, 768), (got768.shape, ref768.shape)
        assert got256.shape == ref256.shape == (1, frames, 256), (got256.shape, ref256.shape)
        for got, ref in ((got768, ref768), (got256, ref256)):
            worst = max(worst, float(np.abs(got - ref).max() / np.abs(ref).max()))

    data = open(out_path, "rb").read()
    print(f"file      {out_path}")
    print(f"size      {len(data)}")
    print(f"sha256    {hashlib.sha256(data).hexdigest()}")
    print(f"parity    worst difference {worst:.2e} of the largest feature value")


if __name__ == "__main__":
    main(sys.argv[1])
