namespace FaceRecognition
{
    /// <summary>
    /// Bir yüz için tanıma sonucu.
    /// Distance: LBPH mesafesi. KÜÇÜK değer = örneklere DAHA BENZER.
    /// Threshold: tahmin yapılırken kullanılan eşik (mesafe bundan büyükse "Bilinmiyor").
    /// </summary>
    public record RecognitionResult(string DisplayName, string NearestLabel, double Distance, bool IsKnown, double Threshold)
    {
        public static RecognitionResult NotTrained { get; } =
            new RecognitionResult("Model eğitilmedi", "", double.NaN, false, FaceRecognizerService.DefaultThreshold);

        /// <summary>
        /// Arayüzde gösterilen benzerlik yüzdesi. Gerçek bir olasılık değildir; mesafeyi eşiğe göre ölçekler:
        ///   mesafe 0          → %100
        ///   mesafe = eşik     → %60  (tanındı / bilinmiyor sınırı)
        ///   mesafe = 2 × eşik → %0
        /// Böylece tanınan yüzler her zaman %60'ın üstünde, tanınmayanlar altında görünür
        /// ve eşik değiştirildiğinde yüzde de buna uyar.
        /// </summary>
        public int ConfidencePercent
        {
            get
            {
                if (double.IsNaN(Distance) || Threshold <= 0) return 0;

                double percent = Distance <= Threshold
                    ? 60 + 40 * (Threshold - Distance) / Threshold
                    : 60 * (1 - (Distance - Threshold) / Threshold);

                return (int)Math.Round(Math.Clamp(percent, 0, 100));
            }
        }
    }
}
