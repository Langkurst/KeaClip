using System.Globalization;
using Avalonia.Data.Converters;
using Klippi.Core;

namespace Klippi.Desktop;

public sealed class ChoiceLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DownloadMode.VideoAudio => "Video + audio", DownloadMode.VideoOnly => "Video only", DownloadMode.Audio => "Audio only", DownloadMode.Archive => "Preservation archive",
        AudioFormat.Original => "Original audio", AudioFormat.Mp3 => "MP3", AudioFormat.Aac => "AAC / M4A", AudioFormat.Opus => "Opus", AudioFormat.Flac => "FLAC", AudioFormat.Wav => "WAV", AudioFormat.Ogg => "Ogg Vorbis",
        VideoCodec.Original => "Original codec", VideoCodec.H264 => "H.264", VideoCodec.Hevc => "HEVC / H.265", VideoCodec.Av1 => "AV1",
        MediaContainer.Auto => "Automatic", MediaContainer.Mp4 => "MP4", MediaContainer.Mkv => "MKV", MediaContainer.WebM => "WebM",
        EncoderPreference.Auto => "Automatic", EncoderPreference.Gpu => "Hardware encoder", EncoderPreference.Cpu => "CPU encoder",
        ClipMode.Lossless => "Lossless · near keyframes", ClipMode.Exact => "Exact · re-encode",
        CompressionPreset.TargetSize => "Target size", CompressionPreset.VerySmall => "Very small",
        CloseBehavior.Exit => "Exit app", CloseBehavior.Tray => "Keep in tray", CloseBehavior.Ask => "Ask each time",
        _ => value?.ToString() ?? ""
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Avalonia.Data.BindingOperations.DoNothing;
}
