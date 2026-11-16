"""Export the FCPE pitch extractor (torchfcpe's bundled model, MIT) to ONNX for MicFX.

The exported graph is the whole of what RVC's real-time code calls:
model.infer(audio, sr=16000, decoder_mode="local_argmax", threshold=0.006),
i.e. mel spectrogram, network and decoder. The mel spectrogram is computed
with a real-valued STFT (convolution with windowed cosines and sines)
because ONNX export does not handle torch.stft's complex output; the script
checks it against torchfcpe's own mel before exporting.

    input  audio  float32 [1, samples]     16 kHz mono
    output f0     float32 [1, samples/160 + 1]  Hz, 0 = unvoiced

Usage: python export_fcpe.py OUT.onnx
Needs torch, torchfcpe 0.0.4, onnx and onnxruntime. Prints the file's
SHA-256 and the largest difference to PyTorch on test signals.
"""

import hashlib
import math
import sys

import types

import numpy as np
import onnxruntime as ort
import torch

try:
    import torchaudio  # noqa: F401 - torchfcpe imports it
except (ImportError, OSError):
    # torchfcpe only uses torchaudio to resample input that is not 16 kHz; MicFX
    # always gives it 16 kHz. Without a torchaudio build for this torch, stand in.
    class _Resample(torch.nn.Module):
        def __init__(self, orig_freq, new_freq, *args, **kwargs):
            super().__init__()
            if orig_freq != new_freq:
                raise NotImplementedError("resampling needs torchaudio")

        def forward(self, x):
            return x

    stub = types.ModuleType("torchaudio")
    stub.transforms = types.ModuleType("torchaudio.transforms")
    stub.transforms.Resample = _Resample
    sys.modules["torchaudio"] = stub
    sys.modules["torchaudio.transforms"] = stub.transforms

from torchfcpe import spawn_bundled_infer_model  # noqa: E402


N_FFT, HOP = 1024, 160


class ConvMel(torch.nn.Module):
    """torchfcpe's Wav2Mel at 16 kHz (n_fft = window = 1024, hop 160, centre off,
    reflect padding of 432 each side, magnitude with 1e-9, log clamp 1e-5)."""

    def __init__(self, mel_basis):
        super().__init__()
        window = torch.hann_window(N_FFT, dtype=torch.float64)
        n = torch.arange(N_FFT, dtype=torch.float64)
        k = torch.arange(N_FFT // 2 + 1, dtype=torch.float64)[:, None]
        angle = 2 * math.pi * k * n / N_FFT
        self.register_buffer("cos", (torch.cos(angle) * window).float()[:, None, :])
        self.register_buffer("sin", (-torch.sin(angle) * window).float()[:, None, :])
        self.register_buffer("mel_basis", mel_basis.float())

    def forward(self, audio):  # [1, L] -> [1, L/160 + 1, 128]
        pad = (N_FFT - HOP) // 2
        y = torch.nn.functional.pad(audio[:, None, :], (pad, pad), mode="reflect")
        re = torch.nn.functional.conv1d(y, self.cos, stride=HOP)
        im = torch.nn.functional.conv1d(y, self.sin, stride=HOP)
        spec = torch.sqrt(re * re + im * im + 1e-9)
        mel = torch.log(torch.clamp(torch.matmul(self.mel_basis, spec), min=1e-5))
        mel = mel.transpose(1, 2)
        return torch.cat((mel, mel[:, -1:, :]), 1)  # one frame more, as torchfcpe pads


class Fcpe(torch.nn.Module):
    def __init__(self):
        super().__init__()
        self.reference = spawn_bundled_infer_model(device="cpu")
        self.mel = ConvMel(self.reference.wav2mel.mel_extractor.mel_basis)
        self.net = self.reference.model

    def forward(self, audio):
        f0 = self.net.infer(self.mel(audio), decoder="local_argmax", threshold=0.006)
        return f0.squeeze(-1)

    def original(self, audio):
        return self.reference.infer(audio, sr=16000, decoder_mode="local_argmax", threshold=0.006).squeeze(-1)


def test_signal(seconds=3.0, seed=0):
    """A sung vowel with vibrato and a glide, pauses and some noise."""
    rng = np.random.default_rng(seed)
    n = int(16000 * seconds)
    t = np.arange(n) / 16000
    f0 = 140 * 2 ** (np.sin(2 * math.pi * 0.4 * t)) * (1 + 0.02 * np.sin(2 * math.pi * 5 * t))
    phase = 2 * math.pi * np.cumsum(f0) / 16000
    voice = sum(np.sin(k * phase) / k for k in range(1, 20))
    gate = ((t % 1.0) < 0.7).astype(np.float64)
    x = 0.2 * voice * gate + 0.003 * rng.standard_normal(n)
    return x.astype(np.float32)[None, :]


def main(out_path):
    model = Fcpe().eval()
    example = torch.from_numpy(test_signal(1.0))
    with torch.no_grad():
        for seconds in (0.5, 1.37):
            x = torch.from_numpy(test_signal(seconds, 7))
            ours, theirs = model.mel(x), model.reference.wav2mel(x, 16000)
            assert ours.shape == theirs.shape, (ours.shape, theirs.shape)
            print(f"mel       max difference {float((ours - theirs).abs().max()):.2e}")
    with torch.no_grad():
        torch.onnx.export(
            model, (example,), out_path,
            input_names=["audio"], output_names=["f0"],
            dynamic_axes={"audio": {1: "samples"}, "f0": {1: "frames"}},
            opset_version=17, dynamo=False)

    session = ort.InferenceSession(out_path, providers=["CPUExecutionProvider"])
    worst_cents, voicing_mismatch, frames = 0.0, 0, 0
    for seconds, seed in [(0.5, 1), (2.0, 2), (3.0, 3)]:
        x = test_signal(seconds, seed)
        with torch.no_grad():
            ref = model.original(torch.from_numpy(x)).numpy()
        got = session.run(["f0"], {"audio": x})[0]
        assert got.shape == ref.shape == (1, x.shape[1] // 160 + 1), (got.shape, ref.shape)
        voiced_ref, voiced_got = ref > 0, got > 0
        voicing_mismatch += int(np.sum(voiced_ref != voiced_got))
        frames += ref.size
        both = voiced_ref & voiced_got
        if both.any():
            cents = np.abs(1200 * np.log2(got[both] / ref[both]))
            worst_cents = max(worst_cents, float(cents.max()))

    digest = hashlib.sha256(open(out_path, "rb").read()).hexdigest()
    size = len(open(out_path, "rb").read())
    print(f"file      {out_path}")
    print(f"size      {size}")
    print(f"sha256    {digest}")
    print(f"parity    worst {worst_cents:.3f} cents on voiced frames, {voicing_mismatch}/{frames} voicing flips")


if __name__ == "__main__":
    main(sys.argv[1])
