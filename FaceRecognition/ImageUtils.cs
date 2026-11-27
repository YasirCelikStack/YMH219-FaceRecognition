using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>
    /// OpenCV görüntüleri (Mat) için küçük yardımcı metotlar.
    /// Dosya okuma/yazma File + ImDecode/ImEncode ile yapılır; böylece Türkçe karakterli
    /// klasör adlarında da sorun çıkmaz (Cv2.ImRead/ImWrite bu yollarda başarısız olabiliyor).
    /// </summary>
    public static class ImageUtils
    {
        /// <summary>Tanıma için tüm yüz kırpıntıları bu boyuta getirilir (LBPH aynı boyut ister).</summary>
        public const int FaceSize = 200;

        /// <summary>Renkli (BGR) görüntüyü gri tonlamaya çevirir ve ışık farklarını azaltmak için histogram eşitler.</summary>
        public static Mat ToGray(Mat bgr)
        {
            var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);
            return gray;
        }

        /// <summary>Gri görüntüden yüz bölgesini kesip FaceSize x FaceSize boyutuna getirir.</summary>
        public static Mat CropFace(Mat gray, Rect faceRect)
        {
            // Dikdörtgen resmin dışına taşmasın
            Rect safe = faceRect.Intersect(new Rect(0, 0, gray.Width, gray.Height));

            using var roi = new Mat(gray, safe);
            var face = new Mat();
            Cv2.Resize(roi, face, new OpenCvSharp.Size(FaceSize, FaceSize));
            return face;
        }

        /// <summary>
        /// 3 kanallı (BGR) Mat'i PictureBox'ta gösterilebilecek bir Bitmap'e çevirir.
        /// Pikseller bellekte satır satır doğrudan kopyalanır (resmi önce .bmp'ye kodlayıp
        /// tekrar çözmekten çok daha hızlıdır). Format24bppRgb bellekte zaten B-G-R sırasındadır.
        /// </summary>
        public static Bitmap ToBitmap(Mat mat)
        {
            if (mat.Type() != MatType.CV_8UC3)
            {
                throw new ArgumentException("3 kanallı (BGR) görüntü bekleniyor.");
            }

            int width = mat.Width;
            int height = mat.Height;
            int rowBytes = width * 3;
            long sourceStep = mat.Step();

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height),
                                              ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] row = new byte[rowBytes];
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(mat.Data, (int)(y * sourceStep)), row, 0, rowBytes);
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }

            return bitmap;
        }

        public static Mat LoadImage(string path, ImreadModes mode)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Mat mat = Cv2.ImDecode(bytes, mode);
            if (mat.Empty())
            {
                mat.Dispose();
                throw new InvalidDataException($"Resim okunamadı: {Path.GetFileName(path)}");
            }
            return mat;
        }

        public static void SaveImage(string path, Mat mat)
        {
            Cv2.ImEncode(".png", mat, out byte[] buffer);
            File.WriteAllBytes(path, buffer);
        }
    }
}
