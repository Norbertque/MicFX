using System.Text.Json;
using MicFX.Models;
using Xunit;

namespace MicFX.Tests;

public class SettingsTests
{
    [Fact]
    public void SettingsFromBeforeTheAiVoice_LoadWithItOff()
    {
        // A 1.11.1 settings file: no AI voice keys at all.
        const string old = """
            { "MicGain": 1.2, "GateEnabled": true, "DenoiseMode": "Ai", "EqBandCount": 10,
              "Profiles": [ { "Name": "Discord", "MicGain": 0.9, "EqGainsDb": [0,0,0,0,0,0,0,0,0,0] } ],
              "AccentColor": "#3FB8AF" }
            """;
        var s = JsonSerializer.Deserialize<AppSettings>(old)!;
        Assert.False(s.AiVoiceEnabled);
        Assert.Null(s.AiVoiceModelPath);
        Assert.Equal(0f, s.AiVoicePitch);
        Assert.Equal("Cuda", s.AiVoiceBackend);
        Assert.Equal("Normal", s.AiVoiceDelay);
        Assert.False(s.AiVoiceNvidiaAccepted);
        Assert.False(s.Profiles[0].AiVoiceEnabled);
        Assert.Null(s.Profiles[0].AiVoiceModelPath);
        Assert.Equal(1.2f, s.MicGain);
        Assert.Equal("#3FB8AF", s.AccentColor);
        Assert.Equal(AppSettings.DefaultAccentColor, new AppSettings().AccentColor);
    }

    [Fact]
    public void AiVoiceSettings_RoundTrip()
    {
        var s = new AppSettings { AiVoiceEnabled = true, AiVoiceModelPath = @"C:\voices\me.onnx", AiVoicePitch = -3, AiVoiceBackend = "Cpu", AiVoiceDelay = "Safe" };
        s.Profiles.Add(new Profile { Name = "Robot", AiVoiceEnabled = true, AiVoicePitch = 12 });
        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;
        Assert.True(back.AiVoiceEnabled);
        Assert.Equal(@"C:\voices\me.onnx", back.AiVoiceModelPath);
        Assert.Equal(-3f, back.AiVoicePitch);
        Assert.Equal("Cpu", back.AiVoiceBackend);
        Assert.Equal("Safe", back.AiVoiceDelay);
        Assert.Equal(12f, back.Profiles[0].AiVoicePitch);
    }
}
