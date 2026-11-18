using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace IGYO
{
    // Picture helpers for New Tab backgrounds and profile pictures.
    static class Pictures
    {
        // Phone photos often say "rotate me" instead of being stored upright.
        public static int Orientation(Image image)
        {
            try
            {
                if (!image.PropertyIdList.Contains(0x0112)) return 1;
                var value = image.GetPropertyItem(0x0112).Value;
                return value != null && value.Length >= 2 ? BitConverter.ToUInt16(value, 0) : 1;
            }
            catch { return 1; }
        }

        public static RotateFlipType FlipFor(int orientation)
        {
            switch (orientation)
            {
                case 2: return RotateFlipType.RotateNoneFlipX;
                case 3: return RotateFlipType.Rotate180FlipNone;
                case 4: return RotateFlipType.Rotate180FlipX;
                case 5: return RotateFlipType.Rotate90FlipX;
                case 6: return RotateFlipType.Rotate90FlipNone;
                case 7: return RotateFlipType.Rotate270FlipX;
                case 8: return RotateFlipType.Rotate270FlipNone;
                default: return RotateFlipType.RotateNoneFlipNone;
            }
        }

        static void HighQuality(Graphics g)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        static void Draw(Graphics g, Image image, Rectangle target, Rectangle source)
        {
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY);   // no dark fringe along the edges
                g.DrawImage(image, target, source.X, source.Y, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
            }
        }

        // A copy no larger than maxSide on its long side, on a dark backing (for JPEG).
        public static Bitmap Scale(Image image, int maxSide)
        {
            double k = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)Math.Round(image.Width * k)), h = Math.Max(1, (int)Math.Round(image.Height * k));
            var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(24, 24, 30));
                HighQuality(g);
                Draw(g, image, new Rectangle(0, 0, w, h), new Rectangle(0, 0, image.Width, image.Height));
            }
            return bmp;
        }

        // A small copy of a picture, out of focus: what a look shows when it lays your picture behind glass (it is
        // never larger than 960 on its long side, so it also costs far less memory to show than the picture itself).
        // The blur is a fixed share of the picture's width as it fills a wide window, so every picture comes out
        // equally soft whatever its size or shape.
        public static Bitmap Soften(Image image)
        {
            using (var small = Scale(image, 480))   // worked on at half the size: it is going to be blurred anyway
            {
                double reach = Math.Min(small.Width, small.Height * 16.0 / 9) * 0.0156;   // how far the blur spreads (its sigma)
                BoxBlur(small, (int)Math.Round((Math.Sqrt(4 * reach * reach + 1) - 1) / 2), 3);
                var bmp = new Bitmap(small.Width * 2, small.Height * 2, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    HighQuality(g);
                    Draw(g, small, new Rectangle(0, 0, bmp.Width, bmp.Height), new Rectangle(0, 0, small.Width, small.Height));
                }
                return bmp;
            }
        }

        // Each pixel becomes the average of its neighbours within the radius, along rows and then columns; done
        // three times, that is close to the soft bell shape of a lens out of focus.
        static void BoxBlur(Bitmap bmp, int radius, int passes)
        {
            if (radius < 1) return;
            var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                int w = bmp.Width, h = bmp.Height, stride = Math.Abs(bits.Stride);
                var a = new byte[stride * h];
                var b = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0, a, 0, a.Length);
                for (int pass = 0; pass < passes; pass++)
                {
                    BlurLines(a, b, w, h, 3, stride, radius);   // along each row
                    BlurLines(b, a, h, w, stride, 3, radius);   // along each column
                }
                System.Runtime.InteropServices.Marshal.Copy(a, 0, bits.Scan0, a.Length);
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
        }

        // One pass along lines of `length` pixels: `step` bytes from a pixel to the next one on its line, `lineStep`
        // bytes from a line to the next. Past either end of a line its last pixel repeats, so edges don't darken.
        static void BlurLines(byte[] from, byte[] to, int length, int lines, int step, int lineStep, int radius)
        {
            int window = radius * 2 + 1;
            for (int line = 0; line < lines; line++)
            {
                int start = line * lineStep;
                for (int channel = 0; channel < 3; channel++)
                {
                    int sum = 0;
                    for (int i = -radius; i <= radius; i++) sum += from[start + Math.Min(length - 1, Math.Max(0, i)) * step + channel];
                    for (int i = 0; i < length; i++)
                    {
                        to[start + i * step + channel] = (byte)((sum + window / 2) / window);
                        int leaving = Math.Max(0, i - radius), entering = Math.Min(length - 1, i + radius + 1);
                        sum += from[start + entering * step + channel] - from[start + leaving * step + channel];
                    }
                }
            }
        }

        // A small picture of a page for the card over its tab: its top, 16:9 (or less when the page is wider), at most
        // width pixels wide, as JPEG bytes. Null if the picture can't be read.
        public static byte[] Thumbnail(byte[] picture, int width)
        {
            try
            {
                using (var stream = new MemoryStream(picture))
                using (var image = Image.FromStream(stream, false, false))
                {
                    int w = image.Width, h = Math.Min(image.Height, (int)Math.Round(image.Width * 9.0 / 16));
                    double k = Math.Min(1.0, (double)width / w);
                    int tw = Math.Max(1, (int)Math.Round(w * k)), th = Math.Max(1, (int)Math.Round(h * k));
                    using (var bmp = new Bitmap(tw, th, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            HighQuality(g);
                            Draw(g, image, new Rectangle(0, 0, tw, th), new Rectangle(0, 0, w, h));
                        }
                        using (var output = new MemoryStream())
                        {
                            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(Encoder.Quality, 78L);
                                bmp.Save(output, codec, parameters);
                            }
                            return output.ToArray();
                        }
                    }
                }
            }
            catch { return null; }
        }

        // A picture as it is sent to a picture search: JPEG, no larger than maxSide on its long side, anything see-through
        // laid on white. A JPEG or PNG that is small enough already goes as it is (null bytes then; width and height are
        // still given). Null and zero sizes when Windows can't read the picture (WebP, AVIF, SVG): it goes as it is.
        public static byte[] ForSearch(byte[] picture, int maxSide, out int width, out int height, out bool asIs)
        {
            width = height = 0;
            asIs = false;
            try
            {
                using (var stream = new MemoryStream(picture))
                using (var image = Image.FromStream(stream, false, false))
                {
                    width = image.Width;
                    height = image.Height;
                    bool plain = image.RawFormat.Guid == ImageFormat.Jpeg.Guid || image.RawFormat.Guid == ImageFormat.Png.Guid;
                    if (plain && width <= maxSide && height <= maxSide && picture.Length <= 3 * 1024 * 1024) { asIs = true; return null; }
                    double k = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
                    int w = Math.Max(1, (int)Math.Round(width * k)), h = Math.Max(1, (int)Math.Round(height * k));
                    using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            HighQuality(g);
                            Draw(g, image, new Rectangle(0, 0, w, h), new Rectangle(0, 0, image.Width, image.Height));
                        }
                        using (var output = new MemoryStream())
                        {
                            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                            using (var parameters = new EncoderParameters(1))
                            {
                                parameters.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
                                bmp.Save(output, codec, parameters);
                            }
                            return output.ToArray();
                        }
                    }
                }
            }
            catch { width = height = 0; return null; }
        }

        // What kind of picture these bytes are, by their first bytes ("" when none of the usual kinds).
        public static string TypeOf(byte[] b)
        {
            if (b == null || b.Length < 12) return "";
            if (b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return "image/png";
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return "image/gif";
            if (b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "image/webp";
            if (b[4] == 0x66 && b[5] == 0x74 && b[6] == 0x79 && b[7] == 0x70 && b[8] == 0x61 && b[9] == 0x76 && b[10] == 0x69 && b[11] == 0x66) return "image/avif";
            if (b[0] == 0x42 && b[1] == 0x4D) return "image/bmp";
            if (b[0] == 0 && b[1] == 0 && b[2] == 1 && b[3] == 0) return "image/x-icon";
            var head = System.Text.Encoding.UTF8.GetString(b, 0, Math.Min(b.Length, 512)).TrimStart('﻿', ' ', '\r', '\n', '\t');
            if (head.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) || (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0)) return "image/svg+xml";
            return "";
        }

        public static void SaveJpeg(Image image, string path, long quality)
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                image.Save(path, codec, parameters);
            }
        }

        // A square from the middle of a picture file, upright, size x size.
        public static Bitmap Square(string file, int size)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
            using (var image = Image.FromStream(stream, true, true))
            {
                int orientation = Orientation(image);
                if (orientation > 1) image.RotateFlip(FlipFor(orientation));
                int side = Math.Min(image.Width, image.Height);
                var source = new Rectangle((image.Width - side) / 2, (image.Height - side) / 2, side, side);
                var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    HighQuality(g);
                    Draw(g, image, new Rectangle(0, 0, size, size), source);
                }
                return bmp;
            }
        }

        // A round copy at a pixel size, with smooth edges.
        public static Bitmap Round(Image source, int size)
        {
            size = Math.Max(1, size);
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var scaled = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(scaled))
                {
                    HighQuality(g);
                    Draw(g, source, new Rectangle(0, 0, size, size), new Rectangle(0, 0, source.Width, source.Height));
                }
                using (var g = Graphics.FromImage(bmp))
                using (var brush = new TextureBrush(scaled, WrapMode.Clamp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.FillEllipse(brush, 0, 0, size, size);
                }
            }
            return bmp;
        }
    }
}
