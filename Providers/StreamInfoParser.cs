#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace Gelato.Providers;

/// <summary>
/// Parses the human-readable strings that Stremio addons put in a stream's
/// name/description/filename (e.g. "Comet 1080p HEVC DDP5.1 MULTI ⚡ [RD]") into structured
/// info. This lets Gelato surface native quality/track badges and a clean version label
/// WITHOUT waiting on an ffprobe, and order versions best-first.
/// </summary>
public static class StreamInfoParser
{
    public readonly record struct ParsedStreamInfo(
        int? Width,
        int? Height,
        string? VideoCodec, // ffmpeg-style codec id: hevc / h264 / av1
        string? VideoCodecLabel, // display: HEVC / H264 / AV1
        string? VideoRange, // SDR / HDR / HDR10+ / DV
        string? AudioCodec, // ffmpeg-style: eac3 / ac3 / dts / truehd / aac
        string? AudioCodecLabel, // display: DDP / DTS / TrueHD / AAC / Atmos
        int? AudioChannels, // 2 / 6 / 8
        string? AudioLanguage, // mul / und / ...
        string? ResolutionLabel, // 2160p / 1080p / 720p / 480p
        long? SizeBytes,
        bool Cached,
        string? DebridTag // RD / PM / AD / TB / DL / OC
    );

    private static readonly RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Regex SizeRegex = new(
        @"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>gib|gb|mib|mb)",
        Opts
    );

    public static ParsedStreamInfo Parse(string? name, string? description, string? filename)
    {
        var text = string.Join(
            "\n",
            new[] { name, description, filename }.Where(s => !string.IsNullOrWhiteSpace(s))
        );
        if (string.IsNullOrWhiteSpace(text))
            return default;

        int? width = null,
            height = null;
        string? resLabel = null;
        if (Regex.IsMatch(text, @"\b(2160p|4k|uhd)\b", Opts))
        {
            (width, height, resLabel) = (3840, 2160, "2160p");
        }
        else if (Regex.IsMatch(text, @"\b1440p\b", Opts))
        {
            (width, height, resLabel) = (2560, 1440, "1440p");
        }
        else if (Regex.IsMatch(text, @"\b(1080p|fhd)\b", Opts))
        {
            (width, height, resLabel) = (1920, 1080, "1080p");
        }
        else if (Regex.IsMatch(text, @"\b(720p|hd)\b", Opts))
        {
            (width, height, resLabel) = (1280, 720, "720p");
        }
        else if (Regex.IsMatch(text, @"\b(480p|sd)\b", Opts))
        {
            (width, height, resLabel) = (854, 480, "480p");
        }

        string? vCodec = null,
            vCodecLabel = null;
        if (Regex.IsMatch(text, @"\b(hevc|h\.?265|x265)\b", Opts))
            (vCodec, vCodecLabel) = ("hevc", "HEVC");
        else if (Regex.IsMatch(text, @"\bav1\b", Opts))
            (vCodec, vCodecLabel) = ("av1", "AV1");
        else if (Regex.IsMatch(text, @"\b(h\.?264|x264|avc)\b", Opts))
            (vCodec, vCodecLabel) = ("h264", "H264");

        string? vRange = null;
        if (Regex.IsMatch(text, @"(dolby\s*vision|dovi|\bdv\b)", Opts))
            vRange = "DV";
        else if (Regex.IsMatch(text, @"(hdr10\+|hdr10plus|hdr\+)", Opts))
            vRange = "HDR10+";
        else if (Regex.IsMatch(text, @"\bhdr\b", Opts))
            vRange = "HDR";

        string? aCodec = null,
            aCodecLabel = null;
        var atmos = Regex.IsMatch(text, @"\batmos\b", Opts);
        if (Regex.IsMatch(text, @"\btruehd\b", Opts))
            (aCodec, aCodecLabel) = ("truehd", atmos ? "TrueHD Atmos" : "TrueHD");
        else if (Regex.IsMatch(text, @"\b(dts[-\s]?hd|dts[-\s]?x|dts)\b", Opts))
            (aCodec, aCodecLabel) = ("dts", "DTS");
        else if (Regex.IsMatch(text, @"\b(ddp|dd\+|eac3|e-?ac-?3)\b", Opts))
            (aCodec, aCodecLabel) = ("eac3", atmos ? "DDP Atmos" : "DDP");
        else if (Regex.IsMatch(text, @"\b(ac3|dd5|dd2|dolby\s*digital)\b", Opts))
            (aCodec, aCodecLabel) = ("ac3", "AC3");
        else if (Regex.IsMatch(text, @"\baac\b", Opts))
            (aCodec, aCodecLabel) = ("aac", "AAC");
        else if (atmos)
            (aCodec, aCodecLabel) = ("eac3", "Atmos");

        int? channels = null;
        if (Regex.IsMatch(text, @"\b7\.1\b", Opts))
            channels = 8;
        else if (Regex.IsMatch(text, @"\b(5\.1)\b", Opts))
            channels = 6;
        else if (Regex.IsMatch(text, @"\b(2\.0|stereo)\b", Opts))
            channels = 2;

        string? lang = null;
        if (Regex.IsMatch(text, @"\b(multi|multilang|dual)\b", Opts))
            lang = "mul";

        long? size = null;
        var sm = SizeRegex.Match(text);
        if (
            sm.Success
            && double.TryParse(
                sm.Groups["n"].Value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var num
            )
        )
        {
            var unit = sm.Groups["u"].Value.ToLowerInvariant();
            var mult = unit switch
            {
                "gb" or "gib" => 1024d * 1024d * 1024d,
                "mb" or "mib" => 1024d * 1024d,
                _ => 0d,
            };
            if (mult > 0)
                size = (long)(num * mult);
        }

        var cached = Regex.IsMatch(text, @"(⚡|\bcached\b|instant)", Opts);
        string? debrid = null;
        var dm = Regex.Match(text, @"\[(?<t>RD|PM|AD|TB|DL|OC)\+?\]", Opts);
        if (dm.Success)
        {
            debrid = dm.Groups["t"].Value.ToUpperInvariant();
            cached = true; // presence of a debrid tag implies a cached/debrid-backed stream
        }

        return new ParsedStreamInfo(
            width,
            height,
            vCodec,
            vCodecLabel,
            vRange,
            aCodec,
            aCodecLabel,
            channels,
            lang,
            resLabel,
            size,
            cached,
            debrid
        );
    }

    /// <summary>
    /// Builds synthetic MediaStream entries (one video, one audio) from parsed info so
    /// clients can render quality/track badges before a real probe runs. Only emits a
    /// stream when there's at least some signal for it.
    /// </summary>
    public static IReadOnlyList<MediaStream> ToMediaStreams(ParsedStreamInfo info, int startIndex)
    {
        var list = new List<MediaStream>(2);
        var idx = startIndex;

        if (info.Height is not null || info.VideoCodec is not null || info.VideoRange is not null)
        {
            var vTitle = JoinNonEmpty(
                " ",
                info.ResolutionLabel,
                info.VideoCodecLabel,
                info.VideoRange
            );
            list.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = idx++,
                    Codec = info.VideoCodec,
                    Width = info.Width,
                    Height = info.Height,
                    Title = string.IsNullOrEmpty(vTitle) ? null : vTitle,
                    IsDefault = true,
                }
            );
        }

        if (info.AudioCodec is not null || info.AudioChannels is not null)
        {
            list.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = idx++,
                    Codec = info.AudioCodec,
                    Channels = info.AudioChannels,
                    Language = info.AudioLanguage,
                    Title = JoinNonEmpty(" ", info.AudioCodecLabel, ChannelLabel(info.AudioChannels)),
                    IsDefault = true,
                }
            );
        }

        return list;
    }

    /// <summary>
    /// Builds a compact, native-looking version label, e.g. "1080p • HEVC • DV • DDP 5.1 • 2.3 GB • RD⚡".
    /// Falls back to the provided raw name when nothing could be parsed.
    /// </summary>
    public static string BuildSourceName(ParsedStreamInfo info, string? fallback)
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrEmpty(info.ResolutionLabel))
            parts.Add(info.ResolutionLabel!);
        if (!string.IsNullOrEmpty(info.VideoCodecLabel))
            parts.Add(info.VideoCodecLabel!);
        if (!string.IsNullOrEmpty(info.VideoRange))
            parts.Add(info.VideoRange!);

        var audio = JoinNonEmpty(" ", info.AudioCodecLabel, ChannelLabel(info.AudioChannels));
        if (!string.IsNullOrEmpty(audio))
            parts.Add(audio);

        if (!string.IsNullOrEmpty(info.AudioLanguage) && info.AudioLanguage == "mul")
            parts.Add("MULTI");

        if (info.SizeBytes is { } b && b > 0)
            parts.Add(FormatSize(b));

        // Use plain ASCII text rather than an emoji/bullet: some native clients (e.g.
        // Neptune) render those glyphs as boxes or dashes.
        if (info.Cached)
            parts.Add(string.IsNullOrEmpty(info.DebridTag) ? "Cached" : info.DebridTag!);

        if (parts.Count == 0)
            return fallback ?? "Stream";

        return string.Join(" | ", parts);
    }

    private static string? ChannelLabel(int? channels) =>
        channels switch
        {
            8 => "7.1",
            6 => "5.1",
            2 => "2.0",
            _ => null,
        };

    private static string FormatSize(long bytes)
    {
        var gb = bytes / (1024d * 1024d * 1024d);
        if (gb >= 1)
            return $"{gb:0.0} GB";
        var mb = bytes / (1024d * 1024d);
        return $"{mb:0} MB";
    }

    private static string JoinNonEmpty(string sep, params string?[] parts)
    {
        return string.Join(
            sep,
            parts.Where(p => !string.IsNullOrWhiteSpace(p))
        );
    }
}
