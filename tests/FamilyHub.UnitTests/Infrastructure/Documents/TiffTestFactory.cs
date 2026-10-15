using BitMiracle.LibTiff.Classic;

namespace FamilyHub.UnitTests.Infrastructure.Documents;

/// <summary>Генерация TIFF-фикстур в памяти через LibTiff.Net — тот же пакет, которым
/// ImageDownscaler декодирует TIFF, так что бинарные файлы в репозитории не нужны.</summary>
internal static class TiffTestFactory
{
    /// <summary>8-битный RGB без сжатия: левая половина <paramref name="left"/>, правая — <paramref name="right"/>.</summary>
    public static byte[] CreateRgb(int width, int height, (byte R, byte G, byte B) left, (byte R, byte G, byte B) right)
    {
        using var stream = new MemoryStream();
        using (var tiff = Tiff.ClientOpen("fixture.tif", "w", stream, new TiffStream())
            ?? throw new InvalidOperationException("LibTiff не открыл поток на запись."))
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, width);
            tiff.SetField(TiffTag.IMAGELENGTH, height);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 3);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiff.SetField(TiffTag.COMPRESSION, Compression.NONE);
            tiff.SetField(TiffTag.ROWSPERSTRIP, height);

            var row = new byte[width * 3];
            for (var x = 0; x < width; x++)
            {
                var (r, g, b) = x < width / 2 ? left : right;
                row[x * 3] = r;
                row[x * 3 + 1] = g;
                row[x * 3 + 2] = b;
            }
            for (var y = 0; y < height; y++)
                tiff.WriteScanline(row, y);
            tiff.WriteDirectory();
            tiff.Flush();
        }
        return stream.ToArray();
    }
}
