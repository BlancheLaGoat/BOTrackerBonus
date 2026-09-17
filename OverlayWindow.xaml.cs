using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace BO1Tracker
{
    public enum OverlayMode { Bo1Bonus, Bo2Bonus, Box }

    public partial class OverlayWindow : Window
    {
        private static readonly string SavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BlackOpsTracker", "save.json");

        private FileSystemWatcher? _watcher;
        private bool _locked = false;
        private readonly MainWindow? _owner;

        public OverlayMode Mode { get; private set; }

        // ── État BO1/BO2 Bonus ───────────────────────────────────────────
        private List<(int Order, string Label, string ImagePath)> _bo1Clicks = [];
        private List<(int Order, string Label, string ImagePath)> _bo2Clicks = [];
        
        

        // ── État Boîte ───────────────────────────────────────────────────
        private List<(int Order, string Location)> _boxVisits   = [];
        private HashSet<string>                     _boxSelected = [];
        private string?                             _lastVisited = null;
        

        // ── Couleurs (correspondant exactement au tracker) ───────────────
        private static readonly Color ColAccent     = (Color)ColorConverter.ConvertFromString("#1D9E75");
        private static readonly Color ColAccentLight = (Color)ColorConverter.ConvertFromString("#2DD4A2");
        private static readonly Color ColAccentDark  = (Color)ColorConverter.ConvertFromString("#0D3327");
        private static readonly Color ColBgCard      = (Color)ColorConverter.ConvertFromString("#1A1A1A");
        private static readonly Color ColBorder      = (Color)ColorConverter.ConvertFromString("#2A2A2A");
        private static readonly Color ColTextSecond  = (Color)ColorConverter.ConvertFromString("#888888");
        private static readonly Color ColYellow      = (Color)ColorConverter.ConvertFromString("#FFD700");
        private static readonly Color ColYellowDark  = (Color)ColorConverter.ConvertFromString("#B8860B");
        private static readonly Color ColBgYellow    = (Color)ColorConverter.ConvertFromString("#3D3000");

        public OverlayWindow(OverlayMode mode = OverlayMode.Bo1Bonus, MainWindow? owner = null)
        {
            InitializeComponent();
            Mode = mode;
            _owner = owner;
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateModeButtons();
            ReadAndRefresh();
            ApplyOpaqueState(startOpaque: true); // ouverture en opaque par défaut

            var dir = Path.GetDirectoryName(SavePath)!;
            Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir, Path.GetFileName(SavePath))
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) =>
                Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)ReadAndRefresh);
        }

        // ── Lecture JSON ─────────────────────────────────────────────────
        private void ReadAndRefresh()
        {
            try
            {
                if (!File.Exists(SavePath)) return;

                string? json = null;
                for (int i = 0; i < 5; i++)
                {
                    try { json = File.ReadAllText(SavePath); break; }
                    catch (IOException) { System.Threading.Thread.Sleep(60); }
                }
                if (json == null) return;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                
                

                _bo1Clicks = ParseClicks(root, "ClickOrder");
                _bo2Clicks = ParseClicks(root, "Bo2ClickOrder");

                // Boîte
                
                _lastVisited   = root.TryGetProperty("LastBoxLocation", out var lb)  ? lb.GetString()  : null;
                _boxVisits   = [];
                _boxSelected = [];
                if (root.TryGetProperty("BoxVisits", out var bv))
                    foreach (var v in bv.EnumerateArray())
                    {
                        var loc = v.TryGetProperty("Location", out var l) ? l.GetString() ?? "" : "";
                        var ord = v.TryGetProperty("Order",    out var o) ? o.GetInt32()        : 0;
                        _boxVisits.Add((ord, loc));
                    }
                if (root.TryGetProperty("BoxSelected", out var bs))
                    foreach (var s in bs.EnumerateArray())
                        _boxSelected.Add(s.GetString() ?? "");

                RebuildCards();
            }
            catch { }
        }

        private static List<(int Order, string Label, string ImagePath)> ParseClicks(JsonElement root, string key)
        {
            var result = new List<(int, string, string)>();
            if (!root.TryGetProperty(key, out var arr)) return result;
            foreach (var v in arr.EnumerateArray())
            {
                int ord = v.TryGetProperty("Order", out var o) ? o.GetInt32() : 0;
                string lbl = "", img = "";
                if (v.TryGetProperty("Drop", out var d))
                {
                    if (d.TryGetProperty("Label",     out var l)) lbl = l.GetString() ?? "";
                    if (d.TryGetProperty("ImagePath", out var p)) img = p.GetString() ?? "";
                }
                result.Add((ord, lbl, img));
            }
            return result;
        }

        // ── Construction cartes ──────────────────────────────────────────
        internal void RebuildCards()
        {
            CardsPanel.Children.Clear();
            RoundSectionPanel.Children.Clear();
            RoundSectionPanel.Visibility = Visibility.Collapsed;

            switch (Mode)
            {
                case OverlayMode.Bo1Bonus:
                    if (_owner != null) BuildBonusCardsLive(_owner.CurrentDropsForOverlay, _owner.SelectedForOverlay, _owner.ClickOrderForOverlay, isBo2: false);
                    else                 BuildBonusCards(_bo1Clicks);
                    BuildBo1RoundSection();
                    break;
                case OverlayMode.Bo2Bonus:
                    if (_owner != null) BuildBonusCardsLive(_owner.CurrentBo2DropsForOverlay, _owner.Bo2SelectedForOverlay, _owner.Bo2ClickOrderForOverlay, isBo2: true);
                    else                 BuildBonusCards(_bo2Clicks);
                    BuildBo2RoundSection();
                    break;
                case OverlayMode.Box:
                    BuildBoxCards();
                    break;
            }

            // Le bouton BONUS n'a de sens qu'en mode Bo1Bonus / Bo2Bonus (pas pour la boîte)
            BonusTypesBtn.Visibility = Mode == OverlayMode.Box ? Visibility.Collapsed : Visibility.Visible;
            if (Mode == OverlayMode.Box) BonusTypesPanel.Visibility = Visibility.Collapsed;
            if (BonusTypesPanel.Visibility == Visibility.Visible) BuildBonusTypesPanel();
        }

        // ── Activer/désactiver les bonus optionnels (Carpenter, Fire Sale, ...) depuis l'overlay ──
        private void BonusTypesButton_Click(object sender, RoutedEventArgs e)
        {
            bool show = BonusTypesPanel.Visibility != Visibility.Visible;
            BonusTypesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) BuildBonusTypesPanel();
        }

        private void BuildBonusTypesPanel()
        {
            BonusTypesPanel.Children.Clear();
            if (_owner == null) return;

            var items = Mode == OverlayMode.Bo2Bonus ? _owner.Bo2ToggleablesForOverlay : _owner.Bo1ToggleablesForOverlay;
            bool isBo2 = Mode == OverlayMode.Bo2Bonus;

            foreach (var (id, label, enabled) in items)
                BonusTypesPanel.Children.Add(MakeBonusTypeChip(id, label, enabled, isBo2));
        }

        private Border MakeBonusTypeChip(string id, string label, bool enabled, bool isBo2)
        {
            var tb = new TextBlock
            {
                Text       = (enabled ? "● " : "○ ") + label.ToUpper(),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(enabled ? ColAccentLight : ColTextSecond),
            };

            var chip = new Border
            {
                Child           = tb,
                Background      = new SolidColorBrush(enabled ? ColAccentDark : ColBgCard),
                BorderBrush     = new SolidColorBrush(enabled ? ColAccent : ColBorder),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(14),
                Padding         = new Thickness(10, 4, 10, 4),
                Margin          = new Thickness(0, 0, 6, 0),
                Cursor          = Cursors.Hand,
                Opacity         = enabled ? 1.0 : 0.65,
            };

            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (_owner == null) return;
                if (isBo2) _owner.ToggleBo2DropEnabledFromOverlay(id);
                else       _owner.ToggleBo1DropEnabledFromOverlay(id);
                RebuildCards(); // reconstruit aussi ce panneau puisqu'il est toujours visible
            };

            return chip;
        }

        /// <summary>Affiche TOUS les bonus disponibles (verts si déjà cliqués, orange pour le dernier en date)
        /// et permet de cliquer directement dans l'overlay pour les activer/désactiver.</summary>
        private void BuildBonusCardsLive(List<DropItem> drops, HashSet<string> selected, List<ClickedDrop> clickOrder, bool isBo2)
        {
            int maxOrder = clickOrder.Count > 0 ? clickOrder.Max(c => c.Order) : 0;
            foreach (var drop in drops)
            {
                var click = clickOrder.FirstOrDefault(c => c.Drop.Id == drop.Id);
                int? order = click?.Order;
                bool isLast = order.HasValue && order.Value == maxOrder && maxOrder > 0;
                CardsPanel.Children.Add(MakeLiveBonusCard(drop, selected.Contains(drop.Id), order, isLast, isBo2));
            }

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "Sélectionne une map dans le tracker...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeLiveBonusCard(DropItem drop, bool isSelected, int? order, bool isLast, bool isBo2)
        {
            var bgColor     = isLast ? ColBgYellow    : (isSelected ? ColAccentDark  : ColBgCard);
            var borderColor = isLast ? ColYellow      : (isSelected ? ColAccent      : ColBorder);
            var labelColor  = isLast ? ColYellow      : (isSelected ? ColAccentLight : ColTextSecond);
            var badgeColor  = isLast ? ColYellowDark  : ColAccent;

            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility          = order.HasValue ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text       = order.HasValue ? $"{order}" : "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            var img = new Image
            {
                Width = 52, Height = 52, Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 6),
                Source = TryLoadImage(drop.ImagePath),
            };

            var lblBlock = new TextBlock
            {
                Text          = drop.Label.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lblBlock);

            var card = new Border
            {
                Child           = stack,
                MinWidth        = 96,
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(isSelected || isLast ? 2 : 1),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
                Cursor          = Cursors.Hand,
            };

            card.Effect = isLast
                ? new DropShadowEffect { Color = ColYellow, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 }
                : isSelected
                    ? new DropShadowEffect { Color = ColAccent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 }
                    : null;

            card.MouseLeftButtonUp += (_, _) =>
            {
                if (_owner == null) return;
                if (isBo2) _owner.ToggleBo2DropFromOverlay(drop.Id);
                else       _owner.ToggleDropFromOverlay(drop.Id);
                RebuildCards();
            };

            return card;
        }

        /// <summary>Section "manche spéciale" pour l'onglet BO1 : uniquement sur les maps qui en ont
        /// vraiment (Kino, Five, Ascension, Shi No Numa, Der Riese), sinon rien.</summary>
        private void BuildBo1RoundSection()
        {
            if (_owner == null || !_owner.IsBo1DogMapActive) return;
            BuildRoundSection(
                title: "MANCHE SPÉCIALE",
                current: _owner.CurrentDogRoundForOverlay,
                steps: [4, 5],
                formatRound: r => _owner.FormatRoundForOverlay(r),
                onAdvance: step => _owner.AdvanceDogRoundFromOverlay(step));
        }

        /// <summary>Section "manche spéciale" pour l'onglet BO2 : Die Rise (+4/+5) ou Mob of the Dead (+3/+4/+5)
        /// selon la map actuellement sélectionnée dans le tracker, sinon rien.</summary>
        private void BuildBo2RoundSection()
        {
            if (_owner == null) return;

            if (_owner.IsBo2DieRiseMapActive)
            {
                BuildRoundSection(
                    title: "MANCHE SPÉCIALE (DIE RISE)",
                    current: _owner.CurrentBo2DogRoundForOverlay,
                    steps: [4, 5],
                    formatRound: r => _owner.FormatRoundForOverlay(r),
                    onAdvance: step => _owner.AdvanceBo2DogRoundFromOverlay(step));
            }
            else if (_owner.IsBo2MobMapActive)
            {
                BuildRoundSection(
                    title: "MANCHE SPÉCIALE (MOB OF THE DEAD)",
                    current: _owner.CurrentBo2MobRoundForOverlay,
                    steps: [4, 5, 6],
                    formatRound: r => $"{r}",
                    onAdvance: step => _owner.AdvanceBo2MobRoundFromOverlay(step));
            }
        }

        private void BuildRoundSection(string title, int? current, int[] steps, Func<int, string> formatRound, Action<int> onAdvance)
        {
            RoundSectionPanel.Visibility = Visibility.Visible;

            var currentBlock = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
            currentBlock.Children.Add(new TextBlock
            {
                Text = title, FontFamily = new FontFamily("Consolas"), FontSize = 10,
                Foreground = new SolidColorBrush(ColTextSecond), Margin = new Thickness(0, 0, 0, 2),
            });
            currentBlock.Children.Add(new TextBlock
            {
                Text = current.HasValue ? formatRound(current.Value) : "—",
                FontFamily = new FontFamily("Consolas"), FontSize = 22, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColAccentLight),
            });
            RoundSectionPanel.Children.Add(currentBlock);

            if (current.HasValue)
                foreach (int step in steps)
                    RoundSectionPanel.Children.Add(MakeRoundStepBadge(step, formatRound(current.Value + step), onAdvance));
        }

        private Border MakeRoundStepBadge(int step, string nextRoundLabel, Action<int> onAdvance)
        {
            var stack = new StackPanel { Orientation = Orientation.Horizontal };
            stack.Children.Add(new TextBlock
            {
                Text = $"+{step} → ", FontFamily = new FontFamily("Consolas"), FontSize = 13,
                Foreground = new SolidColorBrush(ColTextSecond), VerticalAlignment = VerticalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = nextRoundLabel, FontFamily = new FontFamily("Consolas"), FontSize = 15, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(ColAccentLight), VerticalAlignment = VerticalAlignment.Center,
            });

            var badge = new Border
            {
                Child           = stack,
                Background      = new SolidColorBrush(ColAccentDark),
                BorderBrush     = new SolidColorBrush(ColAccent),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(6),
                Padding         = new Thickness(12, 8, 12, 8),
                Margin          = new Thickness(0, 0, 8, 0),
                Cursor          = Cursors.Hand,
            };

            badge.MouseLeftButtonUp += (_, _) =>
            {
                onAdvance(step);
                RebuildCards();
            };

            return badge;
        }


        private void BuildBonusCards(List<(int Order, string Label, string ImagePath)> clicks)
        {
            int maxOrder = clicks.Count > 0 ? clicks.Max(c => c.Order) : 0;
            foreach (var (ord, lbl, imgPath) in clicks.OrderBy(c => c.Order))
                CardsPanel.Children.Add(MakeBonusCard(lbl, imgPath, ord, ord == maxOrder && maxOrder > 0));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier bonus...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBonusCard(string label, string imagePath, int order, bool isLast)
        {
            var bgColor     = isLast ? ColBgYellow  : ColAccentDark;
            var borderColor = isLast ? ColYellow     : ColAccent;
            var labelColor  = isLast ? ColYellow     : ColAccentLight;
            var badgeColor  = isLast ? ColYellowDark : ColAccent;

            // Badge numéro
            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text       = $"{order}",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            // Image du bonus
            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Source              = TryLoadImage(imagePath),
            };

            var lblBlock = new TextBlock
            {
                Text          = label.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lblBlock);

            var card = new Border
            {
                Child           = stack,
                MinWidth        = 96,
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            card.Effect = isLast
                ? new DropShadowEffect { Color = ColYellow,  BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 }
                : new DropShadowEffect { Color = ColAccent,  BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

            return card;
        }

        // ── Cartes Boîte ─────────────────────────────────────────────────
        private void BuildBoxCards()
        {
            int maxOrder = _boxVisits.Count > 0 ? _boxVisits.Max(v => v.Order) : 0;
            foreach (var (ord, loc) in _boxVisits.OrderBy(v => v.Order))
                CardsPanel.Children.Add(MakeBoxCard(loc, ord, ord == maxOrder && maxOrder > 0, false));

            if (_lastVisited != null && !_boxSelected.Contains(_lastVisited))
                CardsPanel.Children.Add(MakeBoxCard(_lastVisited, null, false, true));

            bool empty = CardsPanel.Children.Count == 0;
            EmptyText.Text        = "En attente du premier emplacement...";
            EmptyText.Visibility  = empty ? Visibility.Visible  : Visibility.Collapsed;
            CardsPanel.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        }

        private Border MakeBoxCard(string location, int? order, bool isLast, bool isMemo)
        {
            bool highlight  = isLast || isMemo;
            var bgColor     = highlight ? ColBgYellow : (order.HasValue ? ColAccentDark : ColBgCard);
            var borderColor = highlight ? ColYellow   : (order.HasValue ? ColAccent     : ColBorder);
            var labelColor  = highlight ? ColYellow   : (order.HasValue ? ColAccentLight : ColTextSecond);
            var badgeColor  = highlight ? ColYellowDark : ColAccent;

            var orderBadge = new Border
            {
                Background          = new SolidColorBrush(badgeColor),
                CornerRadius        = new CornerRadius(10),
                Padding             = new Thickness(6, 1, 6, 1),
                Margin              = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Center,
                Visibility          = order.HasValue ? Visibility.Visible : Visibility.Collapsed,
                Child = new TextBlock
                {
                    Text       = order.HasValue ? $"{order}" : "",
                    FontFamily = new FontFamily("Consolas"),
                    FontSize   = 15,
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.White,
                }
            };

            var img = new Image
            {
                Width               = 52,
                Height              = 52,
                Stretch             = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin              = new Thickness(0, 0, 0, 6),
                Source              = TryLoadLocationImage(location),
            };

            var lbl = new TextBlock
            {
                Text          = location.ToUpper(),
                FontFamily    = new FontFamily("Consolas"),
                FontSize      = 11,
                FontWeight    = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping  = TextWrapping.Wrap,
                Foreground    = new SolidColorBrush(labelColor),
                MaxWidth      = 90,
            };

            var stack = new StackPanel { Orientation = Orientation.Vertical, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(orderBadge);
            stack.Children.Add(img);
            stack.Children.Add(new Border { Height = 6 });
            stack.Children.Add(lbl);

            var card = new Border
            {
                Child           = stack,
                MinWidth        = 96,
                Padding         = new Thickness(14, 12, 14, 12),
                CornerRadius    = new CornerRadius(10),
                BorderThickness = new Thickness(highlight || order.HasValue ? 2 : 1),
                Background      = new SolidColorBrush(bgColor),
                BorderBrush     = new SolidColorBrush(borderColor),
                Margin          = new Thickness(0, 0, 12, 0),
            };

            if (highlight)
                card.Effect = new DropShadowEffect { Color = ColYellow, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.6 };
            else if (order.HasValue)
                card.Effect = new DropShadowEffect { Color = ColAccent, BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55 };

            return card;
        }

        // ── Chargement images (pack URI = ressources embarquées) ─────────
        private static BitmapImage? TryLoadImage(string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                var uri = new Uri($"pack://application:,,,/{relativePath}", UriKind.Absolute);
                var bmp = new BitmapImage();
                bmp.BeginInit(); bmp.UriSource = uri; bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit();
                return bmp;
            }
            catch { return null; }
        }

        private static BitmapImage? TryLoadLocationImage(string location)
        {
            return TryLoadImage($"Images/locations/{location}.png")
                ?? TryLoadImage("Images/mystery_box.png");
        }

        // ── Sélecteur de mode ────────────────────────────────────────────
        private void UpdateModeButtons()
        {
            var active   = new SolidColorBrush(Color.FromArgb(200, 29, 158, 117));
            var inactive = new SolidColorBrush(Color.FromArgb(50,  255, 255, 255));
            var fgActive   = Brushes.White;
            var fgInactive = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

            ModeBO1Btn.Background = Mode == OverlayMode.Bo1Bonus ? active : inactive;
            ModeBO2Btn.Background = Mode == OverlayMode.Bo2Bonus ? active : inactive;
            ModeBoxBtn.Background = Mode == OverlayMode.Box      ? active : inactive;
            ModeBO1Btn.Foreground = Mode == OverlayMode.Bo1Bonus ? fgActive : fgInactive;
            ModeBO2Btn.Foreground = Mode == OverlayMode.Bo2Bonus ? fgActive : fgInactive;
            ModeBoxBtn.Foreground = Mode == OverlayMode.Box      ? fgActive : fgInactive;
        }

        private void ModeBO1_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo1Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBO2_Click(object sender, RoutedEventArgs e) { Mode = OverlayMode.Bo2Bonus; UpdateModeButtons(); RebuildCards(); }
        private void ModeBox_Click (object sender, RoutedEventArgs e) { Mode = OverlayMode.Box;      UpdateModeButtons(); RebuildCards(); }

        // ── Drag / contrôles ─────────────────────────────────────────────
        private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!_locked && e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        }

        private void LockButton_Click(object sender, RoutedEventArgs e)
        {
            _locked = !_locked;
            LockBtn.Content = _locked ? "UNLOCK" : "LOCK";
        }

        private void TopButton_Click(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            TopBtn.Content = Topmost ? "FOND" : "DEVANT";
        }

        private bool _opaqueBackground = false;
        private static readonly Brush RootOpaqueBrush        = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0D0D0D"));
        private static readonly Brush HeaderBarDragBrush     = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#01000000")); // quasi invisible mais cliquable
        private static readonly Brush HeaderBarOpaqueBrush   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#DD111111"));
        private static readonly Brush ContentBarOpaqueBrush  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CC111111"));

        /// <summary>Bascule entre fond TOTALEMENT transparent (rien que les cartes flottent à l'écran —
        /// idéal pour OBS) et fond plein (pratique pour utiliser la fenêtre normalement sur le bureau,
        /// sans passer par OBS). La barre du haut garde toujours un fond quasi invisible (alpha=1)
        /// même en mode transparent, sinon la fenêtre ne serait plus déplaçable.</summary>
        private void ApplyOpaqueState(bool startOpaque)
        {
            _opaqueBackground = startOpaque;

            RootGrid.Background          = _opaqueBackground ? RootOpaqueBrush        : Brushes.Transparent;
            HeaderBarBorder.Background   = _opaqueBackground ? HeaderBarOpaqueBrush   : HeaderBarDragBrush;
            ContentBarBorder.Background  = _opaqueBackground ? ContentBarOpaqueBrush  : Brushes.Transparent;

            OpaqueBtn.Content = _opaqueBackground ? "TRANSPARENT" : "OPAQUE";
        }

        private void OpaqueButton_Click(object sender, RoutedEventArgs e) => ApplyOpaqueState(!_opaqueBackground);

        private bool _headerCollapsed = false;

        /// <summary>Réduit la barre du haut aux seuls boutons ▲/▼ + fermeture,
        /// pour n'afficher que les bonus à l'écran (pratique une fois les réglages faits).</summary>
        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            _headerCollapsed = !_headerCollapsed;
            HeaderControlsPanel.Visibility      = _headerCollapsed ? Visibility.Collapsed : Visibility.Visible;
            HeaderRightControlsPanel.Visibility = _headerCollapsed ? Visibility.Collapsed : Visibility.Visible;
            CollapseBtn.Content = _headerCollapsed ? "▼" : "▲";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void OverlayWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
            => _watcher?.Dispose();
    }
}
