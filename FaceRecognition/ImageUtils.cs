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

        /// <summary>Mat'i PictureBox'ta gösterilebilecek bir Bitmap'e çevirir.</summary>
        public static Bitmap ToBitmap(Mat mat)
        {
            Cv2.ImEncode(".bmp", mat, out byte[] buffer);
            using var stream = new MemoryStream(buffer);
            using var temp = new Bitmap(stream);
            return new Bitmap(temp); // kopyası: stream kapansa da bitmap kullanılabilir kalır
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
