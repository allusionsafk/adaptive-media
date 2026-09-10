#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$EnhancementOn,
    [Parameter(Mandatory = $true)][string]$EnhancementOff,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
if (-not ('NativeDvPngComparison' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

public sealed class NativeDvPngImage
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; }
    public int Channels { get; set; }
    public ushort[] Samples { get; set; }
}

public sealed class NativeDvPngResult
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int BitDepth { get; set; }
    public int SampleMaximum { get; set; }
    public long PixelCount { get; set; }
    public long ChangedPixelCount { get; set; }
    public int MaximumChannelDelta { get; set; }
    public double MeanAbsoluteChannelError { get; set; }
    public double NormalizedMeanAbsoluteChannelError { get; set; }
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Right { get; set; }
    public int? Bottom { get; set; }
}

public static class NativeDvPngComparison
{
    private static int ReadInt32BE(BinaryReader reader)
    {
        byte[] b = reader.ReadBytes(4);
        if (b.Length != 4) throw new EndOfStreamException();
        return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
    }

    private static NativeDvPngImage ReadPng(string path)
    {
        byte[] signature = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        using FileStream input = File.OpenRead(path);
        using BinaryReader reader = new BinaryReader(input);
        if (!reader.ReadBytes(8).SequenceEqual(signature)) throw new InvalidDataException("Not a PNG: " + path);
        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = -1;
        using MemoryStream idat = new MemoryStream();
        while (input.Position < input.Length)
        {
            int length = ReadInt32BE(reader);
            string type = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            byte[] data = reader.ReadBytes(length);
            if (data.Length != length) throw new EndOfStreamException();
            reader.ReadBytes(4);
            if (type == "IHDR")
            {
                width = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
                height = (data[4] << 24) | (data[5] << 16) | (data[6] << 8) | data[7];
                bitDepth = data[8]; colorType = data[9]; interlace = data[12];
            }
            else if (type == "IDAT") idat.Write(data, 0, data.Length);
            else if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0 || (bitDepth != 8 && bitDepth != 16) || (colorType != 2 && colorType != 6) || interlace != 0)
            throw new NotSupportedException($"PNG must be non-interlaced RGB/RGBA at 8 or 16 bits (type={colorType}, depth={bitDepth}, interlace={interlace}).");
        int channels = colorType == 2 ? 3 : 4;
        int bytesPerSample = bitDepth / 8;
        int bytesPerPixel = channels * bytesPerSample;
        int rowBytes = checked(width * bytesPerPixel);
        idat.Position = 0;
        using ZLibStream zlib = new ZLibStream(idat, CompressionMode.Decompress);
        using MemoryStream decompressed = new MemoryStream();
        zlib.CopyTo(decompressed);
        byte[] filtered = decompressed.ToArray();
        if (filtered.Length != checked((rowBytes + 1) * height)) throw new InvalidDataException("Unexpected PNG scanline length.");
        byte[] unfiltered = new byte[checked(rowBytes * height)];
        byte[] previous = new byte[rowBytes];
        byte[] current = new byte[rowBytes];
        int sourceOffset = 0;
        for (int y = 0; y < height; y++)
        {
            int filter = filtered[sourceOffset++];
            for (int x = 0; x < rowBytes; x++)
            {
                int raw = filtered[sourceOffset++];
                int a = x >= bytesPerPixel ? current[x - bytesPerPixel] : 0;
                int b = previous[x];
                int c = x >= bytesPerPixel ? previous[x - bytesPerPixel] : 0;
                int value = filter switch
                {
                    0 => raw,
                    1 => raw + a,
                    2 => raw + b,
                    3 => raw + ((a + b) >> 1),
                    4 => raw + Paeth(a, b, c),
                    _ => throw new InvalidDataException("Unsupported PNG filter: " + filter)
                };
                current[x] = (byte)value;
                unfiltered[y * rowBytes + x] = current[x];
            }
            byte[] swap = previous; previous = current; current = swap;
            Array.Clear(current, 0, current.Length);
        }
        ushort[] samples = new ushort[checked(width * height * channels)];
        if (bitDepth == 8)
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = unfiltered[i];
        }
        else
        {
            for (int i = 0; i < samples.Length; i++) samples[i] = (ushort)((unfiltered[i * 2] << 8) | unfiltered[i * 2 + 1]);
        }
        return new NativeDvPngImage { Width = width, Height = height, BitDepth = bitDepth, Channels = channels, Samples = samples };
    }

    public static NativeDvPngResult Compare(string enhancementOn, string enhancementOff)
    {
        NativeDvPngImage on = ReadPng(enhancementOn), off = ReadPng(enhancementOff);
        if (on.Width != off.Width || on.Height != off.Height || on.BitDepth != off.BitDepth || on.Channels != off.Channels)
            throw new InvalidDataException("Capture dimensions, bit depth, and channel layout must match.");
        long changed = 0, pixelCount = (long)on.Width * on.Height, channelCount = pixelCount * 3;
        long absoluteSum = 0; int max = 0;
        int left = on.Width, top = on.Height, right = -1, bottom = -1;
        for (int y = 0; y < on.Height; y++)
        {
            for (int x = 0; x < on.Width; x++)
            {
                int sample = (y * on.Width + x) * on.Channels;
                bool pixelChanged = false;
                for (int channel = 0; channel < 3; channel++)
                {
                    int delta = Math.Abs((int)on.Samples[sample + channel] - off.Samples[sample + channel]);
                    absoluteSum += delta; if (delta > max) max = delta; if (delta != 0) pixelChanged = true;
                }
                if (pixelChanged)
                {
                    changed++; if (x < left) left = x; if (x > right) right = x; if (y < top) top = y; if (y > bottom) bottom = y;
                }
            }
        }
        int sampleMaximum = on.BitDepth == 8 ? 255 : 65535;
        double mae = channelCount == 0 ? 0 : (double)absoluteSum / channelCount;
        return new NativeDvPngResult
        {
            Width = on.Width, Height = on.Height, BitDepth = on.BitDepth, SampleMaximum = sampleMaximum,
            PixelCount = pixelCount, ChangedPixelCount = changed, MaximumChannelDelta = max,
            MeanAbsoluteChannelError = mae, NormalizedMeanAbsoluteChannelError = mae / sampleMaximum,
            Left = changed == 0 ? null : left, Top = changed == 0 ? null : top,
            Right = changed == 0 ? null : right, Bottom = changed == 0 ? null : bottom
        };
    }
}
'@
}

$onPath = (Resolve-Path -LiteralPath $EnhancementOn).Path
$offPath = (Resolve-Path -LiteralPath $EnhancementOff).Path
$output = [IO.Path]::GetFullPath($OutputPath)
$native = [NativeDvPngComparison]::Compare($onPath, $offPath)
$result = [pscustomobject][ordered]@{
    SchemaVersion = 1
    EnhancementOn = [pscustomobject]@{ Path = $onPath; Sha256 = (Get-FileHash -LiteralPath $onPath -Algorithm SHA256).Hash.ToLowerInvariant(); Bytes = (Get-Item -LiteralPath $onPath).Length }
    EnhancementOff = [pscustomobject]@{ Path = $offPath; Sha256 = (Get-FileHash -LiteralPath $offPath -Algorithm SHA256).Hash.ToLowerInvariant(); Bytes = (Get-Item -LiteralPath $offPath).Length }
    Width = $native.Width
    Height = $native.Height
    BitDepth = $native.BitDepth
    SampleMaximum = $native.SampleMaximum
    PixelCount = $native.PixelCount
    ChangedPixelCount = $native.ChangedPixelCount
    ChangedPixelFraction = if ($native.PixelCount) { [double]$native.ChangedPixelCount / $native.PixelCount } else { 0 }
    MaximumChannelDelta = $native.MaximumChannelDelta
    MeanAbsoluteChannelError = $native.MeanAbsoluteChannelError
    NormalizedMeanAbsoluteChannelError = $native.NormalizedMeanAbsoluteChannelError
    BoundingBox = if ($native.ChangedPixelCount) { [pscustomobject]@{ Left = $native.Left; Top = $native.Top; Right = $native.Right; Bottom = $native.Bottom } } else { $null }
}
$parent = Split-Path -Parent $output
if ($parent) { [void][IO.Directory]::CreateDirectory($parent) }
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8NoBOM
Write-Output $result
