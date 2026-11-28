using System.Drawing.Drawing2D;
using System.Text.Json;
using OpenCvSharp;

namespace FaceRecognition
{
    /// <summary>Uygulamanın renkleri (tek yerde toplanır).</summary>
    public static class Palette
    {
        public static readonly System.Drawing.Color Background = System.Drawing.Color.FromArgb(243, 244, 248);
        public static readonly System.Drawing.Color Card = System.Drawing.Color.White;
        public static readonly System.Drawing.Color Text = System.Drawing.Color.FromArgb(30, 33, 40);
        public static readonly System.Drawing.Color Muted = System.Drawing.Color.FromArgb(108, 114, 128);
        public static readonly System.Drawing.Color Border = System.Drawing.Color.FromArgb(218, 221, 230);
        public static readonly System.Drawing.Color Accent = System.Drawing.Color.FromArgb(79, 110, 247);
        public static readonly System.Drawing.Color Known = System.Drawing.Color.FromArgb(46, 160, 67);
        public static readonly System.Drawing.Color Unknown = System.Drawing.Color.FromArgb(240, 110, 40);
        public static readonly System.Drawing.Color Danger = System.Drawing.Color.FromArgb(229, 57, 53);
    }

    /// <summary>Kalıcı ayarlar: tanıma eşiği ve gizlilik modu (ayarlar.json).</summary>
    public class FaceSettings
    {
        public double Threshold { get; set; } = FaceRecognizerService.DefaultThreshold;
        public bool PrivacyMode { get; set; }

        private static string FilePath => Path.Combine(AppContext.BaseDirectory, "ayarlar.json");

        public static FaceSettings Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return new FaceSettings();
                return JsonSerializer.Deserialize<FaceSettings>(File.ReadAllText(FilePath)) ?? new FaceSettings();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new FaceSettings();
            }
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ayar kaydedilemezse uygulama yine çalışır
            }
        }
    }

    /// <summary>
    /// "Tanınan yüzleri klasöre kaydet" özelliği: en büyük yüzün kırpıntısını Captures/&lt;kişi&gt;/ klasörüne yazar.
    /// Diski doldurmamak için en fazla saniyede bir kayıt yapar. Veriler yereldir, hiçbir yere gönderilmez.
    /// </summary>
    public sealed class FaceCaptureWriter
    {
        private static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);
        private DateTime _lastSave = DateTime.MinValue;

        public string RootPath { get; } = Path.Combine(AppContext.BaseDirectory, "Captures");
        public int SavedCount { get; private set; }

        /// <summary>Son kayıttan bu yana yeterli süre geçtiyse yüzü kaydeder. Kaydettiyse true döner.</summary>
        public bool TrySave(Mat face, string label)
        {
            DateTime now = DateTime.Now;
            if (now - _lastSave < MinInterval) return false;

            char[] invalid = Path.GetInvalidFileNameChars();
            string safe = new string(label.Where(c => !invalid.Contains(c)).ToArray()).Trim();
            if (safe.Length == 0) safe = "Bilinmiyor";

            string folder = Path.Combine(RootPath, safe);
            Directory.CreateDirectory(folder);
            ImageUtils.SaveImage(Path.Combine(folder, $"{now:yyyyMMdd_HHmmss_fff}.png"), face);

            _lastSave = now;
            SavedCount++;
            return true;
        }
    }

    /// <summary>Güven yüzdesini yuvarlak köşeli renkli çubukla gösterir.</summary>
    public class ConfidenceBar : Control
    {
        private int _value;
        private System.Drawing.Color _barColor = Palette.Accent;

        public int Value
        {
            get => _value;
            set
            {
                _value = Math.Clamp(value, 0, 100);
                Invalidate();
            }
        }

        public System.Drawing.Color BarColor
        {
            get => _barColor;
            set
            {
                _barColor = value;
                Invalidate();
            }
        }

        public ConfidenceBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 12;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Palette.Card);

            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var trackBrush = new SolidBrush(System.Drawing.Color.FromArgb(232, 234, 240)))
            using (GraphicsPath trackPath = Rounded(track, track.Height / 2))
            {
                g.FillPath(trackBrush, trackPath);
            }

            if (_value <= 0) return;
            var fill = new RectangleF(0, 0, Math.Max(track.Height, track.Width * _value / 100f), track.Height);
            using var fillBrush = new SolidBrush(_barColor);
            using GraphicsPath fillPath = Rounded(fill, fill.Height / 2);
            g.FillPath(fillBrush, fillPath);
        }

        private static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    /// <summary>Tek satırlık metin isteyen küçük pencere (WinForms'ta hazır InputBox olmadığı için).</summary>
    public static class InputDialog
    {
        /// <returns>Girilen metin; vazgeçildiyse null.</returns>
        public static string? Show(IWin32Window owner, string title, string prompt, string initialValue)
        {
            using var form = new Form
            {
                Text = title,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                ShowInTaskbar = false,
                ClientSize = new System.Drawing.Size(360, 130),
                Font = new Font("Segoe UI", 9.75f),
                BackColor = Palette.Card
            };

            var label = new Label { Text = prompt, Left = 14, Top = 14, Width = 330, Height = 22 };
            var textBox = new TextBox { Text = initialValue, Left = 14, Top = 40, Width = 330 };
            var ok = new Button { Text = "Tamam", DialogResult = DialogResult.OK, Left = 168, Top = 82, Width = 84, Height = 30 };
            var cancel = new Button { Text = "Vazgeç", DialogResult = DialogResult.Cancel, Left = 260, Top = 82, Width = 84, Height = 30 };

            form.Controls.AddRange(new Control[] { label, textBox, ok, cancel });
            form.AcceptButton = ok;
            form.CancelButton = cancel;

            return form.ShowDialog(owner) == DialogResult.OK ? textBox.Text.Trim() : null;
        }
    }
}
