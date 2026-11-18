$ErrorActionPreference = 'Stop'
$sampleFolder = Join-Path $PSScriptRoot 'samples'
[System.IO.Directory]::CreateDirectory($sampleFolder) | Out-Null
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

# A valid empty ZIP container deliberately named as a JPEG; the application should flag the mismatch.
$emptyZip = [byte[]](0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)
[System.IO.File]::WriteAllBytes((Join-Path $sampleFolder 'Поддельное-изображение.jpg'), $emptyZip)

# A small archive with a traversal entry name; it is never extracted by the demonstration app.
$memory = [System.IO.MemoryStream]::new()
$archive = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Create, $true)
$entry = $archive.CreateEntry('../outside.txt')
$entryStream = $entry.Open()
$writer = [System.IO.StreamWriter]::new($entryStream, [System.Text.UTF8Encoding]::new($false))
$writer.Write('Safe demo payload; this entry must remain inside its archive.')
$writer.Dispose()
$archive.Dispose()
[System.IO.File]::WriteAllBytes((Join-Path $sampleFolder 'Путь-с-выходом.zip'), $memory.ToArray())
$memory.Dispose()

# A one-page scan with synthetic Russian and English text; it exercises local OCR without network access.
Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(1800, 560)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$font = [System.Drawing.Font]::new('Arial', 82, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$jpegStream = [System.IO.MemoryStream]::new()
try {
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString('LECTURE COURSEWORK 123', $font, [System.Drawing.Brushes]::Black, 40, 45)
    $graphics.DrawString('Учебный конспект', $font, [System.Drawing.Brushes]::Black, 40, 300)
    $bitmap.Save($jpegStream, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $jpeg = $jpegStream.ToArray()
}
finally {
    $jpegStream.Dispose()
    $font.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
$ocrWriterSource = @'
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class FileSorterOcrSampleWriter
{
    public static void Write(string path, byte[] jpeg)
    {
        const int imageWidth = 1800;
        const int imageHeight = 560;
        const int pageWidth = 900;
        const int pageHeight = 280;
        var pageContent = Encoding.ASCII.GetBytes("q 900 0 0 280 0 0 cm /Im0 Do Q");
        var objects = new List<byte[]>
        {
            Encoding.ASCII.GetBytes("<< /Type /Catalog /Pages 2 0 R >>"),
            Encoding.ASCII.GetBytes("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Encoding.ASCII.GetBytes("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + pageWidth + " " + pageHeight + "] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>"),
            BuildStream("<< /Type /XObject /Subtype /Image /Width " + imageWidth + " /Height " + imageHeight + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpeg.Length + " >>", jpeg),
            BuildStream("<< /Length " + pageContent.Length + " >>", pageContent)
        };

        using (var pdf = new MemoryStream())
        {
            WriteAscii(pdf, "%PDF-1.4\n");
            var offsets = new long[objects.Count + 1];
            for (var index = 0; index < objects.Count; index++)
            {
                offsets[index + 1] = pdf.Position;
                WriteAscii(pdf, (index + 1) + " 0 obj\n");
                pdf.Write(objects[index], 0, objects[index].Length);
                WriteAscii(pdf, "\nendobj\n");
            }

            var xrefOffset = pdf.Position;
            WriteAscii(pdf, "xref\n0 " + offsets.Length + "\n0000000000 65535 f \n");
            for (var index = 1; index < offsets.Length; index++)
                WriteAscii(pdf, offsets[index].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            WriteAscii(pdf, "trailer\n<< /Size " + offsets.Length + " /Root 1 0 R >>\nstartxref\n" + xrefOffset + "\n%%EOF\n");
            File.WriteAllBytes(path, pdf.ToArray());
        }
    }

    private static byte[] BuildStream(string dictionary, byte[] contents)
    {
        using (var stream = new MemoryStream())
        {
            WriteAscii(stream, dictionary + "\nstream\n");
            stream.Write(contents, 0, contents.Length);
            WriteAscii(stream, "\nendstream");
            return stream.ToArray();
        }
    }

    private static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }
}
'@
Add-Type -TypeDefinition $ocrWriterSource
[FileSorterOcrSampleWriter]::Write((Join-Path $sampleFolder 'Скан-конспект.pdf'), $jpeg)
