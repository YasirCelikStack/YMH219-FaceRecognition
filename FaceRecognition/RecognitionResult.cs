namespace FaceRecognition
{
    /// <summary>
    /// Bir yüz için tanıma sonucu.
    /// Distance: LBPH mesafesi. KÜÇÜK değer = örneklere DAHA BENZER.
    /// </summary>
    public record RecognitionResult(string DisplayName, string NearestLabel, double Distance, bool IsKnown)
    {
        public static RecognitionResult NotTrained { get; } =
            new RecognitionResult("Model eğitilmedi", "", double.NaN, false);

        /// <summary>
        /// Arayüzde göstermek için kaba bir güven yüzdesi (100 - mesafe, 0..100 arasına sıkıştırılmış).
        /// Gerçek bir olasılık değildir; sadece "ne kadar benzer" fikri verir.
        /// </summary>
        public int ConfidencePercent =>
            double.IsNaN(Distance) ? 0 : (int)Math.Clamp(100 - Distance, 0, 100);
    }
}
