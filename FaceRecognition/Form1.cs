using OpenCvSharp;

namespace FaceRecognition
{
    public partial class Form1 : Form
    {
        // ---------- Servisler (iş mantığı) ----------
        private readonly CameraService _camera = new();
        private readonly FaceRecognizerService _recognizer = new();
        private readonly FaceDataset _dataset = new(Path.Combine(AppContext.BaseDirectory, "Dataset"));
        private FaceDetector? _detector; // model dosyası yoksa null kalır

        private readonly Mat _frame = new();     // kameradan okunan anlık kare
        private readonly Mat _lastFrame = new(); // ekrandaki son görüntü (örnek eklerken kullanılır)

        // Kamera görüntüsü yaklaşık 30 kare/saniye okunur
        private readonly System.Windows.Forms.Timer _cameraTimer = new() { Interval = 33 };

        // ---------- Arayüz ----------
        private readonly PictureBox _pictureBox = new()
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = System.Drawing.Color.Black
        };

        private readonly Button _btnStartCamera = new() { Text = "Kamera Başlat", Width = 230, Height = 32 };
        private readonly Button _btnStopCamera = new() { Text = "Kamera Durdur", Width = 230, Height = 32, Enabled = false };
        private readonly Button _btnSelectImage = new() { Text = "Görüntü Seç...", Width = 230, Height = 32 };
        private readonly TextBox _txtLabel = new() { Width = 230, PlaceholderText = "Etiket (ör. Yasir)" };
        private readonly Button _btnAddSample = new() { Text = "Yüz Örneği Ekle", Width = 230, Height = 32 };
        private readonly Button _btnTrain = new() { Text = "Modeli Eğit", Width = 230, Height = 32 };
        private readonly Label _lblDataset = new() { Width = 230, Height = 90 };
        private readonly Label _lblResult = new()
        {
            Width = 230,
            Height = 110,
            Font = new Font("Segoe UI", 11, FontStyle.Bold)
        };

        private readonly StatusStrip _statusStrip = new();
        private readonly ToolStripStatusLabel _lblStatus = new();

        private readonly Font _boxFont = new("Segoe UI", 12, FontStyle.Bold);

        public Form1()
        {
            InitializeComponent();
            BuildUi();

            _btnStartCamera.Click += BtnStartCamera_Click;
            _btnStopCamera.Click += (s, e) => StopCamera();
            _btnSelectImage.Click += BtnSelectImage_Click;
            _btnAddSample.Click += BtnAddSample_Click;
            _btnTrain.Click += BtnTrain_Click;
            _cameraTimer.Tick += CameraTimer_Tick;
            FormClosing += Form1_FormClosing;

            LoadDetector();
            TryAutoTrain();
            UpdateDatasetInfo();
        }

        private void BuildUi()
        {
            Text = "Yüz Algılama ve Tanıma - YMH219";
            Size = new System.Drawing.Size(1000, 620);
            MinimumSize = new System.Drawing.Size(800, 500);
            StartPosition = FormStartPosition.CenterScreen;

            var side = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                Width = 255,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(10)
            };

            side.Controls.Add(MakeHeader("Kaynak"));
            side.Controls.Add(_btnStartCamera);
            side.Controls.Add(_btnStopCamera);
            side.Controls.Add(_btnSelectImage);

            side.Controls.Add(MakeHeader("Eğitim"));
            side.Controls.Add(_txtLabel);
            side.Controls.Add(_btnAddSample);
            side.Controls.Add(_btnTrain);
            side.Controls.Add(_lblDataset);

            side.Controls.Add(MakeHeader("Sonuç"));
            side.Controls.Add(_lblResult);

            _statusStrip.Items.Add(_lblStatus);

            // Dock sırası: Fill önce, kenarlar sonra
            Controls.Add(_pictureBox);
            Controls.Add(side);
            Controls.Add(_statusStrip);

            SetStatus("Hazır. Kamerayı başlatın veya bir görüntü seçin.", System.Drawing.Color.Gray);
        }

        private static Label MakeHeader(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 10, FontStyle.Bold),
            Margin = new Padding(0, 12, 0, 4)
        };

        // ---------- Başlangıç ----------

        private void LoadDetector()
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Models", "haarcascade_frontalface_default.xml");
            try
            {
                _detector = new FaceDetector(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or InvalidOperationException)
            {
                _detector = null;
                MessageBox.Show($"{ex.Message}\n\nBeklenen konum:\n{path}\n\nYüz algılama devre dışı.",
                    "Model dosyası hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>Daha önce eklenmiş örnek varsa açılışta modeli otomatik eğitir.</summary>
        private void TryAutoTrain()
        {
            if (_dataset.GetAllSamples().Count == 0) return;
            try
            {
                _recognizer.Train(_dataset);
            }
            catch (Exception ex)
            {
                SetStatus($"Model otomatik eğitilemedi: {ex.Message}", System.Drawing.Color.DarkOrange);
            }
        }

        // ---------- Kamera ----------

        private async void BtnStartCamera_Click(object? sender, EventArgs e)
        {
            _btnStartCamera.Enabled = false;
            SetStatus("Kamera açılıyor...", System.Drawing.Color.DarkOrange);

            bool opened;
            try
            {
                opened = await _camera.StartAsync(0);
            }
            catch (Exception ex)
            {
                opened = false;
                SetStatus($"Kamera hatası: {ex.Message}", System.Drawing.Color.Red);
            }

            if (!opened)
            {
                _btnStartCamera.Enabled = true;
                SetStatus("Kamera açılamadı.", System.Drawing.Color.Red);
                MessageBox.Show("Kamera bulunamadı veya başka bir uygulama tarafından kullanılıyor.",
                    "Kamera hatası", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _btnStopCamera.Enabled = true;
            _cameraTimer.Start();
            SetStatus("● Kamera açık", System.Drawing.Color.Green);
        }

        private void StopCamera()
        {
            _cameraTimer.Stop();
            _camera.Stop();
            _btnStartCamera.Enabled = true;
            _btnStopCamera.Enabled = false;
            SetStatus("Kamera durduruldu.", System.Drawing.Color.Gray);
        }

        private void CameraTimer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (!_camera.ReadFrame(_frame)) return; // bu karede görüntü gelmedi, sonrakini bekle
                ProcessAndShow(_frame);
            }
            catch (OpenCVException ex)
            {
                StopCamera();
                MessageBox.Show($"Görüntü işlenirken hata oluştu:\n{ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- Görüntü dosyası ----------

        private void BtnSelectImage_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Filter = "Resim dosyaları|*.jpg;*.jpeg;*.png;*.bmp|Tüm dosyalar|*.*"
            };
            if (dialog.ShowDialog() != DialogResult.OK) return;

            if (_camera.IsRunning) StopCamera();

            try
            {
                using Mat image = ImageUtils.LoadImage(dialog.FileName, ImreadModes.Color);
                ProcessAndShow(image);
                SetStatus($"Görüntü: {Path.GetFileName(dialog.FileName)}", System.Drawing.Color.Black);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OpenCVException)
            {
                MessageBox.Show($"Görüntü açılamadı:\n{ex.Message}", "Hata",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------- Algılama + tanıma + çizim ----------

        private void ProcessAndShow(Mat bgr)
        {
            bgr.CopyTo(_lastFrame);

            var results = new List<(Rect Box, RecognitionResult Result)>();

            if (_detector != null)
            {
                using Mat gray = ImageUtils.ToGray(bgr);
                foreach (Rect box in _detector.Detect(gray))
                {
                    using Mat face = ImageUtils.CropFace(gray, box);
                    results.Add((box, _recognizer.Predict(face)));
                }
            }

            Bitmap bitmap = ImageUtils.ToBitmap(bgr);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                foreach (var (box, result) in results)
                {
                    DrawFaceBox(g, box, result);
                }
            }

            // Eski görüntüyü bellekten sil, yenisini göster
            Image? old = _pictureBox.Image;
            _pictureBox.Image = bitmap;
            old?.Dispose();

            ShowResultText(results);
        }

        private void DrawFaceBox(Graphics g, Rect box, RecognitionResult result)
        {
            System.Drawing.Color color = !_recognizer.IsTrained ? System.Drawing.Color.DeepSkyBlue
                                        : result.IsKnown ? System.Drawing.Color.LimeGreen
                                        : System.Drawing.Color.OrangeRed;

            using var pen = new Pen(color, 3);
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);

            string text = _recognizer.IsTrained
                ? $"{result.DisplayName} (%{result.ConfidencePercent})"
                : "Yüz";

            SizeF textSize = g.MeasureString(text, _boxFont);
            float textY = Math.Max(0, box.Y - textSize.Height);

            using var background = new SolidBrush(color);
            g.FillRectangle(background, box.X, textY, textSize.Width, textSize.Height);
            g.DrawString(text, _boxFont, Brushes.Black, box.X, textY);
        }

        private void ShowResultText(List<(Rect Box, RecognitionResult Result)> results)
        {
            if (_detector == null)
            {
                _lblResult.Text = "Yüz algılama kapalı\n(model dosyası yok)";
                return;
            }

            if (results.Count == 0)
            {
                _lblResult.Text = "Yüz bulunamadı";
                return;
            }

            // En büyük (kameraya en yakın) yüzün sonucunu göster
            var main = results.OrderByDescending(r => r.Box.Width * r.Box.Height).First().Result;

            if (!_recognizer.IsTrained)
            {
                _lblResult.Text = $"Algılanan yüz: {results.Count}\nTanıma için önce örnek ekleyip modeli eğitin.";
                return;
            }

            _lblResult.Text =
                $"Algılanan yüz: {results.Count}\n" +
                $"Tahmin: {main.DisplayName}\n" +
                $"En yakın: {main.NearestLabel}\n" +
                $"Mesafe: {main.Distance:0.0} (eşik {FaceRecognizerService.UnknownThreshold})\n" +
                $"Güven: %{main.ConfidencePercent}";
        }

        // ---------- Eğitim ----------

        private void BtnAddSample_Click(object? sender, EventArgs e)
        {
            string label = _txtLabel.Text.Trim();
            if (label.Length == 0)
            {
                MessageBox.Show("Önce bir etiket (isim) yazın.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (_detector == null || _lastFrame.Empty())
            {
                MessageBox.Show("Önce kamerayı başlatın veya bir görüntü seçin.", "Bilgi",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using Mat gray = ImageUtils.ToGray(_lastFrame);
                Rect[] faces = _detector.Detect(gray);
                if (faces.Length == 0)
                {
                    MessageBox.Show("Görüntüde yüz bulunamadı.", "Bilgi", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Birden fazla yüz varsa en büyüğünü al
                Rect biggest = faces.OrderByDescending(f => f.Width * f.Height).First();
                using Mat face = ImageUtils.CropFace(gray, biggest);
                _dataset.AddSample(label, face);

                UpdateDatasetInfo();
                SetStatus($"'{label}' için örnek eklendi. (Modeli yeniden eğitmeyi unutmayın)", System.Drawing.Color.Green);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OpenCVException)
            {
                MessageBox.Show($"Örnek kaydedilemedi:\n{ex.Message}", "Hata", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnTrain_Click(object? sender, EventArgs e)
        {
            try
            {
                Cursor = Cursors.WaitCursor;
                TrainResult result = _recognizer.Train(_dataset);

                string message = $"Model eğitildi.\nÖrnek: {result.SampleCount}\nKişi (etiket): {result.LabelCount}";
                if (result.SkippedCount > 0) message += $"\nOkunamayan dosya: {result.SkippedCount}";
                if (result.LabelCount < 2) message += "\n\nNot: Anlamlı bir tanıma için en az 2 farklı etiket ekleyin.";

                MessageBox.Show(message, "Eğitim", MessageBoxButtons.OK, MessageBoxIcon.Information);
                SetStatus("Model eğitildi.", System.Drawing.Color.Green);
            }
            catch (Exception ex) when (ex is InvalidOperationException or OpenCVException)
            {
                MessageBox.Show(ex.Message, "Eğitim yapılamadı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void UpdateDatasetInfo()
        {
            var counts = _dataset.GetSampleCounts();
            if (counts.Count == 0)
            {
                _lblDataset.Text = "Henüz örnek yok.\nHer kişi için 10-20 örnek önerilir.";
                return;
            }

            _lblDataset.Text = "Örnekler:\n" + string.Join("\n", counts.Select(c => $"• {c.Key}: {c.Value}"));
        }

        // ---------- Yardımcılar ----------

        private void SetStatus(string text, System.Drawing.Color color)
        {
            _lblStatus.Text = text;
            _lblStatus.ForeColor = color;
        }

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _cameraTimer.Stop();
            _camera.Dispose();
            _detector?.Dispose();
            _recognizer.Dispose();
            _frame.Dispose();
            _lastFrame.Dispose();
            _pictureBox.Image?.Dispose();
        }
    }
}
