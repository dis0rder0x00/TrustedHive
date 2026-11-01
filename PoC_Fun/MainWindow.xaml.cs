using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using Microsoft.Win32;

namespace TrustedHive;

public partial class MainWindow : Window
{
    // --- Color tokens ---
    static readonly Color CDeskBg = C("#07090a");
    static readonly Color CWindowFace = C("#14191a");
    static readonly Color CPanelRaised = C("#101615");
    static readonly Color CPanelInset = C("#0d1211");
    static readonly Color CFieldBg = C("#050807");
    static readonly Color CHeaderBg = C("#05100c");
    static readonly Color CPrimaryGreen = C("#3dff9e");
    static readonly Color CGreenLight = C("#dfffee");
    static readonly Color CGreenMid = C("#6fd6a5");
    static readonly Color CGreenDim = C("#4e8a70");
    static readonly Color CBodyText = C("#bcd8cc");
    static readonly Color CAccentPink = C("#ff3fa4");
    static readonly Color CPinkLight = C("#ffd8ea");
    static readonly Color CBaseLine = C("#1a2220");
    static readonly Color CBevelLight1 = C("#46564f");
    static readonly Color CBevelLight2 = C("#5f7a72");
    static readonly Color CBevelDark1 = C("#050707");
    static readonly Color CBevelDark2 = C("#060908");
    static readonly Color CBevelDark3 = C("#3c4c47");
    static readonly Color CButtonFace = C("#26332f");
    static readonly Color CButtonHover = C("#2f423c");
    static readonly Color CCaptionTop = C("#1f4f3d");
    static readonly Color CCaptionMid = C("#0d2a21");
    static readonly Color CCaptionBot = C("#123a2d");

    // --- Font families ---
    static readonly FontFamily PixelFont = new(new Uri("pack://application:,,,/"), AppConfig.PixelFontFamily);
    static readonly FontFamily TerminalFont = new(new Uri("pack://application:,,,/"), AppConfig.TerminalFontFamily);
    static readonly FontFamily UiFont = new(AppConfig.UiFontFamily);
    static readonly FontFamily MonoFont = new(AppConfig.MonoFontFamily);
    static readonly FontFamily DisplayFont = new(new Uri("pack://application:,,,/"), AppConfig.DisplayFontFamily);

    // --- State ---
    string _inputPath = "";
    string _outputDir = "";
    string _outputName = "";
    bool _nameTouched;
    string _inputMeta = "no payload loaded";
    string _status = "idle";
    int _volume = 55;
    bool _musicOn;

    // --- UI refs ---
    Border _windowOuter = null!;
    StackPanel _windowStack = null!;
    TextBlock _statusText = null!;
    TextBlock _metaText = null!;
    TextBox _inputBox = null!;
    TextBox _outputDirBox = null!;
    TextBox _outputNameBox = null!;
    TextBlock _nameSourceBadge = null!;
    Button _musicBtn = null!;
    TextBlock _volumeText = null!;
    Canvas _scrollerCanvas = null!;
    Canvas _dvdCanvas = null!;
    Grid _rootGrid = null!;

    // --- Scroller state ---
    readonly List<TextBlock> _scrollerChars = [];
    double _scrollX;
    double _scrollT;
    DateTime _lastActivity = DateTime.Now;
    TextBlock? _dvdLogo;
    double _dvdX = 40, _dvdY = 20, _dvdVx = 1.5, _dvdVy = 0.84;
    DateTime _lastRenderTime = DateTime.MinValue;
    int _dvdHueIdx;
    int _cornerCount;
    DateTime _cornerAt;
    static readonly int[] DvdHues = [150, 300, 190, 45, 0, 265];
    bool _dvdMode;

    // --- CRT jolt ---
    bool _crtBusy;

    // --- Audio ---
    ChiptuneEngine? _chiptune;

    // --- Modals ---
    Grid? _helpOverlay;
    Grid? _genOverlay;
    StackPanel? _genLogPanel;
    ScrollViewer? _genLogScroll;
    Border? _genProgressBar;
    Border? _genProgressFrame;
    TextBlock? _genPctText;
    TextBlock? _genTitleText;
    Button? _genActionBtn;
    bool _genDone;
    CancellationTokenSource? _genCts;
    Thread? _genThread;

    // --- Render loop ---
    bool _renderRunning;

    static Color C(string hex)
    {
        hex = hex.TrimStart('#');
        byte r = Convert.ToByte(hex[0..2], 16);
        byte g = Convert.ToByte(hex[2..4], 16);
        byte b = Convert.ToByte(hex[4..6], 16);
        return Color.FromRgb(r, g, b);
    }

    static SolidColorBrush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static SolidColorBrush B(string hex) => B(C(hex));

    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        BuildUi();
        StartRenderLoop();
        InitAudio();
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _renderRunning = false;
        _genCts?.Cancel();
        _chiptune?.Dispose();
    }

    // =============================================
    // BEVEL HELPER: two nested borders for per-side coloring
    // outer = top+left color, inner = bottom+right color
    // =============================================
    static Border MakeRaisedBevel(UIElement child, Color light, Color dark, Thickness thickness)
    {
        var outer = new Border
        {
            BorderBrush = B(light),
            BorderThickness = new Thickness(thickness.Left, thickness.Top, 0, 0),
        };
        var inner = new Border
        {
            BorderBrush = B(dark),
            BorderThickness = new Thickness(0, 0, thickness.Right, thickness.Bottom),
            Child = child,
        };
        outer.Child = inner;
        return outer;
    }

    static Border MakeInsetBevel(UIElement child, Color dark, Color light, Thickness thickness)
    {
        return MakeRaisedBevel(child, dark, light, thickness);
    }

    // ========== UI CONSTRUCTION ==========

    void BuildUi()
    {
        _rootGrid = new Grid { Background = Brushes.Transparent };

        // Outer desk background — tight padding, no extra black border
        var desk = new Border
        {
            Background = B(CDeskBg),
            Padding = new Thickness(0),
        };

        // Main window — use nested bevels for the classic raised look
        // 1px black outline, then 2px bevel
        var blackOutline = new Border
        {
            BorderBrush = B(Colors.Black),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0),
        };

        var bevelContent = new Border { Background = B(CWindowFace) };
        _windowOuter = MakeRaisedBevel(bevelContent, C("#4a5a56"), CBevelDark1, new Thickness(2, 2, 2, 2));
        blackOutline.Child = _windowOuter;

        _windowStack = new StackPanel();
        bevelContent.Child = _windowStack;

        _windowStack.Children.Add(BuildTitleBar());
        _windowStack.Children.Add(BuildHeader());
        _windowStack.Children.Add(BuildAudioBar());
        _windowStack.Children.Add(BuildBody());
        _windowStack.Children.Add(BuildScroller());
        _windowStack.Children.Add(BuildBaseStrip());

        // Fix width on the outermost content
        blackOutline.Width = 960 + 1 * 2 + 2 * 2 + 2; // content + outline + bevel

        desk.Child = blackOutline;
        _rootGrid.Children.Add(desk);
        Content = _rootGrid;

        // Track activity for screensaver
        MouseMove += (_, _) => _lastActivity = DateTime.Now;
        MouseDown += (_, _) => _lastActivity = DateTime.Now;
        KeyDown += (_, _) => _lastActivity = DateTime.Now;
        MouseWheel += (_, _) => _lastActivity = DateTime.Now;
    }

    // --- Title bar ---
    Border BuildTitleBar()
    {
        var bar = new Border
        {
            Height = 26,
            Background = MakeCaptionGradient(),
            BorderBrush = B(CBevelDark1),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };

        var grid = new Grid();
        bar.Child = grid;

        var left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        // App icon
        left.Children.Add(new Border
        {
            Width = 12, Height = 12,
            Background = B(CPrimaryGreen),
            BorderBrush = B(CDeskBg),
            BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 0, 8, 0),
        });

        left.Children.Add(new TextBlock
        {
            Text = AppConfig.TitleText,
            FontFamily = UiFont, FontSize = 11,
            Foreground = B(C("#bff5da")),
            VerticalAlignment = VerticalAlignment.Center,
        });

        grid.Children.Add(left);

        // Window buttons
        var btns = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        btns.Children.Add(MakeCaptionButton("_", C("#2c3a36"), C("#6a7d78"), CGreenLight,
            () => { WindowState = WindowState.Minimized; }));
        btns.Children.Add(MakeCaptionButton("×", C("#5c2038"), C("#a3667f"), CPinkLight,
            () => { Close(); }));

        grid.Children.Add(btns);

        // Enable dragging — use preview event and only on left-click to reduce lag
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1)
            {
                e.Handled = true;
                DragMove();
            }
        };

        return bar;
    }

    Border MakeCaptionButton(string glyph, Color face, Color lightEdge, Color textColor, Action onClick)
    {
        var content = new Grid { Background = B(face) };
        content.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = UiFont, FontSize = 10,
            Foreground = B(textColor),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var btn = MakeRaisedBevel(content, lightEdge, CBevelDark1, new Thickness(1, 1, 1, 1));
        btn.Width = 20;
        btn.Height = 18;
        btn.Cursor = Cursors.Hand;
        btn.PreviewMouseLeftButtonDown += (_, e) => { e.Handled = true; };
        btn.PreviewMouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        btn.MouseEnter += (_, _) => content.Background = B(Color.FromArgb(200, face.R, face.G, face.B));
        btn.MouseLeave += (_, _) => content.Background = B(face);
        return btn;
    }

    // --- Pixel art header ---
    Border BuildHeader()
    {
        var container = new Border
        {
            Height = 190,
            Background = B(CHeaderBg),
            BorderBrush = B(CBevelDark1),
            BorderThickness = new Thickness(0, 0, 0, 2),
            ClipToBounds = true,
        };

        var grid = new Grid();
        container.Child = grid;

        // Radial glow
        var glowBrush = new RadialGradientBrush
        {
            Center = new Point(0.5, 1.2), GradientOrigin = new Point(0.5, 1.2),
            RadiusX = 1.2, RadiusY = 0.9,
            GradientStops =
            {
                new(Color.FromArgb(41, 61, 255, 158), 0),
                new(Colors.Transparent, 0.62),
            },
        };
        grid.Children.Add(new Rectangle { Fill = glowBrush, IsHitTestVisible = false });

        // CRT scanlines
        AddCrtOverlay(grid, 0.055, addVignette: false);

        // Grid crawl overlay — animate brush transform for seamless infinite tiling
        var gridBrush = CreateGridBrush();
        var brushTransform = new TranslateTransform(0, 0);
        gridBrush.Transform = brushTransform;
        var gridOverlay = new Rectangle
        {
            Fill = gridBrush,
            IsHitTestVisible = false,
        };
        grid.Children.Add(gridOverlay);

        // Centered content
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Wordmark with chromatic aberration and layered shadows
        var wordmarkContainer = new Grid { HorizontalAlignment = HorizontalAlignment.Center };

        // Shadow layer 3 (deepest) — dark green, offset 0,7
        var shadow3 = MakeWordmarkRow(C("#041d13"));
        shadow3.RenderTransform = new TranslateTransform(0, 7);
        wordmarkContainer.Children.Add(shadow3);

        // Shadow layer 2 — mid green, offset 0,6
        var shadow2 = MakeWordmarkRow(C("#08402a"));
        shadow2.RenderTransform = new TranslateTransform(0, 6);
        wordmarkContainer.Children.Add(shadow2);

        // Shadow layer 1 — light green, offset 0,3
        var shadow1 = MakeWordmarkRow(C("#0b5e3c"));
        shadow1.RenderTransform = new TranslateTransform(0, 3);
        wordmarkContainer.Children.Add(shadow1);

        // Chromatic aberration — pink offset right+down
        var caberPink = MakeWordmarkRow(Color.FromArgb(140, 255, 63, 164));
        caberPink.RenderTransform = new TranslateTransform(4, 4);
        wordmarkContainer.Children.Add(caberPink);

        // Chromatic aberration — cyan offset left+up
        var caberCyan = MakeWordmarkRow(Color.FromArgb(89, 63, 214, 255));
        caberCyan.RenderTransform = new TranslateTransform(-4, -2);
        wordmarkContainer.Children.Add(caberCyan);

        // Main wordmark (on top)
        var wordmarkPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        wordmarkPanel.Children.Add(MakeWordmarkPart(AppConfig.Wordmark, CGreenLight));
        wordmarkPanel.Children.Add(MakeWordmarkPart(AppConfig.WordmarkDot, CAccentPink));
        wordmarkPanel.Children.Add(MakeWordmarkPart(AppConfig.WordmarkSuffix, CGreenLight));

        // Green outer glow on main text
        wordmarkPanel.Effect = new DropShadowEffect
        {
            Color = CPrimaryGreen, BlurRadius = 26, ShadowDepth = 0, Opacity = 0.5,
        };
        wordmarkContainer.Children.Add(wordmarkPanel);

        content.Children.Add(wordmarkContainer);

        // Subtitle row
        var subRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
        };

        subRow.Children.Add(new Border
        {
            Width = 120, Height = 2, VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(Colors.Transparent, CPrimaryGreen, 0),
            Margin = new Thickness(0, 0, 12, 0),
        });

        subRow.Children.Add(new TextBlock
        {
            Text = AppConfig.Subtitle,
            FontFamily = DisplayFont, FontSize = 11,
            Foreground = B(C("#8fe9c0")),
            Effect = new DropShadowEffect
            {
                Color = C("#062a1c"), BlurRadius = 0, ShadowDepth = 2.8,
                Direction = 315, Opacity = 1,
            },
        });

        subRow.Children.Add(new Border
        {
            Width = 120, Height = 2, VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(CPrimaryGreen, Colors.Transparent, 0),
            Margin = new Thickness(12, 0, 0, 0),
        });

        content.Children.Add(subRow);

        // Greetz line
        content.Children.Add(new TextBlock
        {
            Text = AppConfig.GreetzLine,
            FontFamily = DisplayFont, FontSize = 19,
            Foreground = B(C("#5fbf92")),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        grid.Children.Add(content);

        // Bottom vignette removed — it created a visible bright band on the dark header

        // Animate grid crawl — shift brush origin for seamless infinite scroll
        var crawlAnim = new DoubleAnimation(0, -32, TimeSpan.FromSeconds(1.3))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        brushTransform.BeginAnimation(TranslateTransform.YProperty, crawlAnim);

        return container;
    }

    TextBlock MakeWordmarkPart(string text, Color color)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = DisplayFont, FontSize = 44,
            Foreground = B(color),
        };
    }

    StackPanel MakeWordmarkRow(Color color)
    {
        var fullText = AppConfig.Wordmark + AppConfig.WordmarkDot + AppConfig.WordmarkSuffix;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(new TextBlock { Text = fullText, FontFamily = DisplayFont, FontSize = 44, Foreground = B(color) });
        return row;
    }

    // --- Audio / help bar ---
    Border BuildAudioBar()
    {
        var bar = new Border
        {
            Background = B(CPanelRaised),
            BorderBrush = B(CBevelDark1),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 8, 12, 8),
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        // Music panel (inset frame)
        var musicRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        _musicBtn = MakeThemedButton("♪ MUSIC : OFF", 104, OnMusicToggle);
        musicRow.Children.Add(_musicBtn);

        musicRow.Children.Add(new TextBlock
        {
            Text = "VOL", FontFamily = TerminalFont, FontSize = 11,
            Foreground = B(CGreenMid),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        });

        var slider = new Slider
        {
            Minimum = 0, Maximum = 100, Value = _volume,
            Width = 110, Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        };
        slider.ValueChanged += OnVolumeChanged;
        musicRow.Children.Add(slider);

        _volumeText = new TextBlock
        {
            Text = _volume.ToString(),
            FontFamily = TerminalFont, FontSize = 13,
            Foreground = B(CPrimaryGreen),
            Width = 30, TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        musicRow.Children.Add(_volumeText);

        var musicContent = new Border
        {
            Background = B(C("#1a2220")),
            Padding = new Thickness(9, 5, 9, 5),
            Child = musicRow,
        };
        var musicFrame = MakeInsetBevel(musicContent, CBevelDark1, CBevelLight1, new Thickness(1, 1, 1, 1));
        row.Children.Add(musicFrame);

        // Help button
        var helpContent = new Grid { Background = B(C("#3d1729")) };
        helpContent.Children.Add(new TextBlock
        {
            Text = "?", FontFamily = PixelFont, FontSize = 12,
            Foreground = B(CPinkLight),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var helpBtn = MakeRaisedBevel(helpContent, C("#8d5470"), CBevelDark2, new Thickness(2, 2, 2, 2));
        helpBtn.Width = 34;
        helpBtn.Margin = new Thickness(14, 0, 0, 0);
        helpBtn.Cursor = Cursors.Hand;
        helpBtn.ToolTip = "About";
        helpBtn.MouseEnter += (_, _) => helpContent.Background = B(C("#55203a"));
        helpBtn.MouseLeave += (_, _) => helpContent.Background = B(C("#3d1729"));
        helpBtn.MouseLeftButtonUp += (_, _) => ShowHelpModal();
        row.Children.Add(helpBtn);

        bar.Child = row;
        return bar;
    }

    // --- Body (2-column) ---
    Grid BuildBody()
    {
        var body = new Grid { Background = B(CWindowFace) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(206) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); // gap
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftCol = BuildLeftColumn();
        Grid.SetColumn(leftCol, 0);
        body.Children.Add(leftCol);

        var rightCol = BuildRightColumn();
        Grid.SetColumn(rightCol, 2);
        body.Children.Add(rightCol);

        body.Margin = new Thickness(16);
        return body;
    }

    // --- Left column ---
    DockPanel BuildLeftColumn()
    {
        var col = new DockPanel { LastChildFill = true };

        // --- Profile group (heading + avatar + contact) in one raised panel ---
        var profileStack = new StackPanel();

        profileStack.Children.Add(new TextBlock
        {
            Text = "The dev:", FontFamily = TerminalFont, FontSize = 14,
            Foreground = B(CGreenDim), Margin = new Thickness(0, 0, 0, 6),
        });

        // Avatar frame
        var avatarInner = new Border
        {
            Height = 182,
            Background = CreateDiagonalStripeBrush(),
            BorderBrush = B(C("#3a4a45")),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
        };
        UIElement avatarContent;
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri(AppConfig.AvatarResourcePath, UriKind.Relative));
            if (sri != null)
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = sri.Stream;
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                avatarContent = new Image { Source = bmp, Stretch = Stretch.UniformToFill };
            }
            else throw new Exception("Resource not found");
        }
        catch
        {
            avatarContent = new TextBlock
            {
                Text = "[ avatar ]\n194 × 182\ndrop art here",
                FontFamily = MonoFont, FontSize = 11,
                Foreground = B(C("#6b8f80")),
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 17.6,
            };
        }
        avatarInner.Child = avatarContent;
        avatarInner.MouseLeftButtonUp += (_, _) => TriggerCrtJolt();
        profileStack.Children.Add(avatarInner);

        // Contact section inside the same group
        profileStack.Children.Add(new Border { Height = 10 });
        profileStack.Children.Add(new TextBlock
        {
            Text = "CONTACT",
            FontFamily = TerminalFont, FontSize = 13,
            Foreground = B(CPrimaryGreen),
            Margin = new Thickness(0, 0, 0, 3),
        });

        foreach (var (prefix, label, url) in AppConfig.ContactLinks)
        {
            var linkRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
            linkRow.Children.Add(new TextBlock
            {
                Text = prefix, FontFamily = TerminalFont, FontSize = 14,
                Foreground = B(CGreenDim), Margin = new Thickness(0, 0, 6, 0),
            });

            var linkText = new TextBlock
            {
                Text = label, FontFamily = TerminalFont, FontSize = 14,
                Foreground = B(CGreenMid), Cursor = Cursors.Hand,
            };
            var capturedUrl = url;
            linkText.MouseEnter += (_, _) => linkText.Foreground = B(CAccentPink);
            linkText.MouseLeave += (_, _) => linkText.Foreground = B(CGreenMid);
            linkText.MouseLeftButtonUp += (_, _) => Process.Start(new ProcessStartInfo(capturedUrl) { UseShellExecute = true });
            linkRow.Children.Add(linkText);
            profileStack.Children.Add(linkRow);
        }

        var profilePadding = new Border { Background = B(CPanelInset), Padding = new Thickness(9, 8, 9, 8), Child = profileStack };
        var profileFrame = MakeInsetBevel(profilePadding, CBevelDark1, CBevelLight1, new Thickness(1, 1, 1, 1));
        DockPanel.SetDock(profileFrame, Dock.Top);
        col.Children.Add(profileFrame);

        // --- Hint box (docked to bottom, aligns with GENERATE button) ---
        var hintRow = new WrapPanel();
        hintRow.Children.Add(new TextBlock
        {
            Text = "> ",
            FontFamily = TerminalFont, FontSize = 14,
            Foreground = B(CAccentPink),
        });

        var hintText = new TextBlock
        {
            FontFamily = TerminalFont, FontSize = 14,
            Foreground = B(CGreenMid),
            TextWrapping = TextWrapping.Wrap,
        };
        hintText.Inlines.Add("Click the ");
        hintText.Inlines.Add(new System.Windows.Documents.Run("?") { FontWeight = FontWeights.Bold, Foreground = B(CGreenLight) });
        hintText.Inlines.Add(" in the upper right corner for help / info.");
        hintRow.Children.Add(hintText);

        var hintPadding = new Border { Background = B(CPanelInset), Padding = new Thickness(9, 8, 9, 8), Child = hintRow };
        var hintFrame = MakeInsetBevel(hintPadding, CBevelDark1, CBevelLight1, new Thickness(1, 1, 1, 1));
        hintFrame.VerticalAlignment = VerticalAlignment.Bottom;
        col.Children.Add(hintFrame);

        return col;
    }

    // --- Right column ---
    StackPanel BuildRightColumn()
    {
        var col = new StackPanel();

        // Step 1
        col.Children.Add(BuildStepPanel("STEP 1", "#3dff9e", "SELECT PAYLOAD", null, ref _inputBox,
            "C:\\path\\to\\payload.dll", "FILE…", OnPickFile));

        _metaText = new TextBlock
        {
            Text = _inputMeta,
            FontFamily = TerminalFont, FontSize = 12,
            Foreground = B(CGreenDim),
            Margin = new Thickness(14, -8, 14, 0),
        };
        col.Children.Add(_metaText);

        // Step 2
        col.Children.Add(BuildStepPanel("STEP 2", "#3dff9e", "SELECT OUTPUT FOLDER", null, ref _outputDirBox,
            "C:\\output\\", "FOLDER…", OnPickFolder));

        // Step 3
        col.Children.Add(BuildStepPanel("STEP 3", "#8fe9c0", "OUTPUT FILE NAME", "(optional)", ref _outputNameBox,
            "auto — taken from payload", "RE-SYNC", OnResync, true));

        // Action row
        var actionRow = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });

        // GENERATE button
        var genContent = new Border
        {
            Background = MakeGenerateGradient(false),
            Padding = new Thickness(0, 14, 0, 14),
        };
        var expText = new TextBlock
        {
            Text = "EXPLOIT",
            FontFamily = PixelFont, FontSize = 14,
            Foreground = B(CDeskBg),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        genContent.Child = expText;
        var genBtn = MakeRaisedBevel(genContent, C("#d6ffe9"), C("#06301f"), new Thickness(3, 3, 3, 3));
        genBtn.Cursor = Cursors.Hand;
        genBtn.MouseEnter += (_, _) => genContent.Background = MakeGenerateGradient(true);
        genBtn.MouseLeave += (_, _) => genContent.Background = MakeGenerateGradient(false);
        genBtn.MouseLeftButtonUp += (_, _) => OnExploit();
        Grid.SetColumn(genBtn, 0);
        actionRow.Children.Add(genBtn);

        // STATUS readout
        var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        statusStack.Children.Add(new TextBlock
        {
            Text = "STATUS",
            FontFamily = TerminalFont, FontSize = 12,
            Foreground = B(CGreenDim),
        });
        _statusText = new TextBlock
        {
            Text = _status,
            FontFamily = TerminalFont, FontSize = 15,
            Foreground = B(CPrimaryGreen),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        statusStack.Children.Add(_statusText);
        var statusContent = new Border { Background = B(CFieldBg), Padding = new Thickness(10, 8, 10, 8), Child = statusStack };
        var statusBox = MakeInsetBevel(statusContent, CBevelDark1, CBevelDark3, new Thickness(2, 2, 2, 2));
        Grid.SetColumn(statusBox, 2);
        actionRow.Children.Add(statusBox);

        col.Children.Add(actionRow);

        // Wire up text changed events
        _inputBox.TextChanged += OnInputPathChanged;
        _outputDirBox.TextChanged += (_, _) => _outputDir = _outputDirBox.Text;
        _outputNameBox.TextChanged += OnOutputNameChanged;

        return col;
    }

    Border BuildStepPanel(string chip, string chipBg, string title, string? optional,
        ref TextBox textBox, string placeholder, string btnText, Action btnClick,
        bool hasNameSource = false)
    {
        var stack = new StackPanel();

        // Header row
        var headerRow = new DockPanel { Margin = new Thickness(0, 0, 0, 9) };

        var chipBlock = new Border { Background = B(chipBg), Padding = new Thickness(6, 5, 6, 5) };
        chipBlock.Child = new TextBlock
        {
            Text = chip, FontFamily = PixelFont, FontSize = 9, Foreground = B(CDeskBg),
        };
        DockPanel.SetDock(chipBlock, Dock.Left);
        headerRow.Children.Add(chipBlock);

        var titleBlock = new TextBlock
        {
            Text = title, FontFamily = DisplayFont, FontSize = 15, FontWeight = FontWeights.Bold,
            Foreground = B(CGreenLight), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        DockPanel.SetDock(titleBlock, Dock.Left);
        headerRow.Children.Add(titleBlock);

        if (optional != null)
        {
            var optBlock = new TextBlock
            {
                Text = optional, FontFamily = UiFont, FontSize = 11,
                Foreground = B(C("#5f8a78")), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(optBlock, Dock.Left);
            headerRow.Children.Add(optBlock);
        }

        if (hasNameSource)
        {
            _nameSourceBadge = new TextBlock
            {
                Text = "auto", FontFamily = TerminalFont, FontSize = 12,
                Foreground = B(CGreenMid), VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            DockPanel.SetDock(_nameSourceBadge, Dock.Right);
            headerRow.Children.Add(_nameSourceBadge);
        }

        // Filler line
        headerRow.Children.Add(new Border
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
            Background = new LinearGradientBrush(C("#2b3a35"), Colors.Transparent, 0),
        });
        stack.Children.Add(headerRow);

        // Input row
        var inputRow = new Grid();
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(132) });

        textBox = new TextBox
        {
            FontFamily = MonoFont, FontSize = 13,
            Foreground = B(CPrimaryGreen),
            Background = B(CFieldBg),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(9, 7, 9, 7),
            CaretBrush = B(CPrimaryGreen),
            SpellCheck = { IsEnabled = false },
        };
        // Wrap textbox in inset bevel
        var tbInset = MakeInsetBevel(textBox, CBevelDark1, CBevelDark3, new Thickness(2, 2, 2, 2));
        Grid.SetColumn(tbInset, 0);
        inputRow.Children.Add(tbInset);
        SetPlaceholder(textBox, placeholder);

        var btn = MakeThemedButton(btnText, 132, btnClick);
        Grid.SetColumn(btn, 2);
        inputRow.Children.Add(btn);

        stack.Children.Add(inputRow);

        var panelContent = new Border
        {
            Background = B(CPanelRaised),
            Padding = new Thickness(14, 12, 14, 14),
            Child = stack,
        };
        var panel = MakeRaisedBevel(panelContent, CBevelLight1, CBevelDark1, new Thickness(1, 1, 1, 1));
        panel.Margin = new Thickness(0, 0, 0, 14);
        return panel;
    }

    // --- Scroller ---
    Border BuildScroller()
    {
        var container = new Border
        {
            Height = 78,
            Background = B(CFieldBg),
            BorderBrush = B(CBevelDark1),
            BorderThickness = new Thickness(0, 2, 0, 0),
            ClipToBounds = true,
        };

        var grid = new Grid();
        container.Child = grid;

        // CRT scanlines + vignette for scroller
        AddCrtOverlay(grid, 0.06);

        // Sine text canvas \u2014 has green glow effect
        _scrollerCanvas = new Canvas
        {
            ClipToBounds = true,
            Effect = new DropShadowEffect
            {
                Color = CPrimaryGreen, BlurRadius = 14, ShadowDepth = 2,
                Direction = 270, Opacity = 0.45,
            },
        };
        grid.Children.Add(_scrollerCanvas);

        var text = AppConfig.ScrollerText;
        _scrollX = 960;
        foreach (char ch in text)
        {
            var tb = new TextBlock
            {
                Text = ch == ' ' ? "\u00a0" : ch.ToString(),
                FontFamily = DisplayFont, FontSize = 20,
                Visibility = Visibility.Hidden,
            };
            _scrollerCanvas.Children.Add(tb);
            _scrollerChars.Add(tb);
        }

        // DVD logo canvas \u2014 separate so it gets its own color glow
        _dvdCanvas = new Canvas { ClipToBounds = true };
        grid.Children.Add(_dvdCanvas);

        _dvdLogo = new TextBlock
        {
            Text = "TRUSTME.GEN",
            FontFamily = DisplayFont, FontSize = 15,
            Foreground = B(CPrimaryGreen),
            Padding = new Thickness(10, 6, 10, 6),
            Visibility = Visibility.Hidden,
        };
        _dvdCanvas.Children.Add(_dvdLogo);

        // Edge fades — fully opaque at edges, transparent in center
        var leftFade = new Rectangle
        {
            Width = 70, HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
        };
        leftFade.Fill = new LinearGradientBrush(
            new GradientStopCollection { new(CFieldBg, 0), new(Color.FromArgb(0, 5, 8, 7), 1) },
            new Point(0, 0.5), new Point(1, 0.5));
        grid.Children.Add(leftFade);

        var rightFade = new Rectangle
        {
            Width = 70, HorizontalAlignment = HorizontalAlignment.Right,
            IsHitTestVisible = false,
        };
        rightFade.Fill = new LinearGradientBrush(
            new GradientStopCollection { new(Color.FromArgb(0, 5, 8, 7), 0), new(CFieldBg, 1) },
            new Point(0, 0.5), new Point(1, 0.5));
        grid.Children.Add(rightFade);

        // Bottom bevel line
        grid.Children.Add(new Rectangle
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = B(CBevelLight1),
            IsHitTestVisible = false,
        });

        return container;
    }

    Border BuildBaseStrip() => new() { Height = 8, Background = B(CBaseLine) };

    // ========== RENDER LOOP ==========

    void StartRenderLoop()
    {
        _renderRunning = true;
        CompositionTarget.Rendering += OnRenderFrame;
    }

    void OnRenderFrame(object? sender, EventArgs e)
    {
        if (!_renderRunning) return;
        var now = DateTime.Now;
        double dt = _lastRenderTime == DateTime.MinValue ? 1.0 : (now - _lastRenderTime).TotalMilliseconds / 16.667;
        _lastRenderTime = now;
        if (dt > 4) dt = 4;
        UpdateScroller(dt);
    }

    void UpdateScroller(double dt)
    {
        var activeCanvas = _dvdMode ? _dvdCanvas : _scrollerCanvas;
        double w = activeCanvas.ActualWidth;
        double h = activeCanvas.ActualHeight;
        if (w <= 0) w = _scrollerCanvas.ActualWidth;
        if (h <= 0) h = _scrollerCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        bool idle = (DateTime.Now - _lastActivity).TotalSeconds > 20;

        if (idle)
        {
            if (!_dvdMode)
            {
                _dvdMode = true;
                _scrollerCanvas.Visibility = Visibility.Hidden;
                _dvdCanvas.Visibility = Visibility.Visible;
                _dvdLogo!.Visibility = Visibility.Visible;
                PaintDvdLogo();
            }

            double lw = _dvdLogo!.ActualWidth > 0 ? _dvdLogo.ActualWidth : 180;
            double lh = _dvdLogo.ActualHeight > 0 ? _dvdLogo.ActualHeight : 30;

            _dvdX += _dvdVx * dt;
            _dvdY += _dvdVy * dt;

            int hits = 0;
            if (_dvdX <= 0) { _dvdX = 0; _dvdVx = Math.Abs(_dvdVx); hits++; }
            if (_dvdX + lw >= w) { _dvdX = w - lw; _dvdVx = -Math.Abs(_dvdVx); hits++; }
            if (_dvdY <= 0) { _dvdY = 0; _dvdVy = Math.Abs(_dvdVy); hits++; }
            if (_dvdY + lh >= h) { _dvdY = h - lh; _dvdVy = -Math.Abs(_dvdVy); hits++; }

            if (hits > 0)
            {
                _dvdHueIdx++;
                PaintDvdLogo();
                if (hits > 1) { _cornerCount++; _cornerAt = DateTime.Now; }
            }

            bool fresh = _cornerCount > 0 && (DateTime.Now - _cornerAt).TotalSeconds < 4;
            _dvdLogo.Text = _cornerCount > 0
                ? (fresh ? $"★ CORNER HIT! ×{_cornerCount}" : $"TRUSTME.GEN  ×{_cornerCount}")
                : "TRUSTME.GEN";

            Canvas.SetLeft(_dvdLogo, _dvdX);
            Canvas.SetTop(_dvdLogo, _dvdY);
            return;
        }

        if (_dvdMode)
        {
            _dvdMode = false;
            _dvdLogo!.Visibility = Visibility.Hidden;
            _dvdCanvas.Visibility = Visibility.Hidden;
            _scrollerCanvas.Visibility = Visibility.Visible;
        }

        // Doubled speed: 4.4 px/frame, phase 0.09/frame
        const double speed = 4.4;
        const int charWidth = 21;

        _scrollX -= speed * dt;
        _scrollT += 0.09 * dt;
        int total = _scrollerChars.Count * charWidth;
        if (_scrollX < -total) _scrollX = w;

        for (int i = 0; i < _scrollerChars.Count; i++)
        {
            double px = _scrollX + i * charWidth;
            var s = _scrollerChars[i];

            if (px < -charWidth || px > w + charWidth)
            {
                s.Visibility = Visibility.Hidden;
                continue;
            }

            s.Visibility = Visibility.Visible;
            double phase = _scrollT - i * 0.28;
            double py = (h - 24) / 2 + Math.Sin(phase) * 20 + Math.Sin(phase * 0.53) * 6;
            double sc = 1 + Math.Sin(phase) * 0.12;

            Canvas.SetLeft(s, px);
            Canvas.SetTop(s, py);
            s.RenderTransform = new ScaleTransform(sc, sc);

            double lum = 62 + Math.Sin(phase) * 22;
            double hue = 150 + Math.Sin(phase * 0.7) * 40;
            s.Foreground = new SolidColorBrush(HslToColor(hue, 1.0, lum / 100.0));
        }
    }

    void PaintDvdLogo()
    {
        int hue = DvdHues[_dvdHueIdx % DvdHues.Length];
        var color = HslToColor(hue, 1.0, 0.68);
        var shadowColor = HslToColor(hue, 0.9, 0.18);
        _dvdLogo!.Foreground = B(color);
        _dvdLogo.Effect = new DropShadowEffect
        {
            Color = color, BlurRadius = 16, ShadowDepth = 2,
            Direction = 270, Opacity = 0.6,
        };
    }

    // ========== CRT JOLT ==========

    void TriggerCrtJolt()
    {
        if (_crtBusy || _windowOuter == null) return;
        _crtBusy = true;

        var transformGroup = new TransformGroup();
        var translate = new TranslateTransform();
        var skew = new SkewTransform();
        var scale = new ScaleTransform(1, 1);
        transformGroup.Children.Add(translate);
        transformGroup.Children.Add(skew);
        transformGroup.Children.Add(scale);
        _windowOuter.RenderTransformOrigin = new Point(0.5, 0.5);
        _windowOuter.RenderTransform = transformGroup;

        // Color distortion overlay
        var colorOverlay = new Rectangle
        {
            Fill = Brushes.Transparent,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        _rootGrid.Children.Add(colorOverlay);

        // Scanline roll bar
        var scanBar = new Rectangle
        {
            Height = 200,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Opacity = 0,
        };
        scanBar.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new(Colors.Transparent, 0),
                new(Color.FromArgb(41, 61, 255, 158), 0.3),
                new(Color.FromArgb(31, 255, 63, 164), 0.7),
                new(Colors.Transparent, 1),
            }, new Point(0.5, 0), new Point(0.5, 1));
        scanBar.RenderTransform = new TranslateTransform(0, -200);
        _rootGrid.Children.Add(scanBar);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900.0 / 24) };
        int step = 0;
        var rng = new Random();
        double windowH = ActualHeight > 0 ? ActualHeight : 780;

        timer.Tick += (_, _) =>
        {
            step++;
            if (step >= 24)
            {
                timer.Stop();
                _windowOuter.RenderTransform = Transform.Identity;
                colorOverlay.Opacity = 0;
                scanBar.Opacity = 0;
                _rootGrid.Children.Remove(colorOverlay);
                _rootGrid.Children.Remove(scanBar);
                _crtBusy = false;
                return;
            }

            // Position jitter
            translate.X = (rng.NextDouble() - 0.5) * 8;
            translate.Y = (rng.NextDouble() - 0.5) * 6;
            skew.AngleX = (rng.NextDouble() - 0.5) * 3;
            scale.ScaleY = 1 + (rng.NextDouble() - 0.5) * 0.04;

            // Color distortion — alternate between green/pink/cyan tints
            double t = step / 24.0;
            int colorIdx = step % 4;
            var tintColor = colorIdx switch
            {
                0 => Color.FromArgb(50, 61, 255, 158),   // green
                1 => Color.FromArgb(40, 255, 63, 164),    // pink
                2 => Color.FromArgb(35, 63, 214, 255),    // cyan
                _ => Color.FromArgb(30, 255, 200, 60),    // warm
            };
            colorOverlay.Fill = new SolidColorBrush(tintColor);
            colorOverlay.Opacity = 0.3 + rng.NextDouble() * 0.4;

            // Rolling scanline bar
            scanBar.Opacity = 0.6;
            double barY = -200 + (step / 24.0) * (windowH + 400);
            ((TranslateTransform)scanBar.RenderTransform).Y = barY;
        };
        timer.Start();
    }

    // ========== AUDIO ==========

    void InitAudio()
    {
        try
        {
            _chiptune = new ChiptuneEngine();
            _chiptune.Volume = _volume / 100f;
            _chiptune.Play();
            _musicOn = true;
            UpdateMusicButton();
        }
        catch { /* Audio init failed — continue without music */ }
    }

    void OnMusicToggle()
    {
        if (_chiptune == null) return;
        if (_musicOn)
        {
            _chiptune.Stop();
            _musicOn = false;
        }
        else
        {
            _chiptune.Play();
            _musicOn = true;
        }
        UpdateMusicButton();
    }

    void UpdateMusicButton()
    {
        if (_musicBtn == null) return;
        UpdateButtonText(_musicBtn, _musicOn ? "♪ MUSIC : ON" : "♪ MUSIC : OFF");
    }

    void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _volume = (int)e.NewValue;
        _volumeText.Text = _volume.ToString();
        if (_chiptune != null)
            _chiptune.Volume = _volume / 100f;
    }

    // ========== FILE PICKERS ==========

    void OnPickFile()
    {
        var dlg = new OpenFileDialog { Title = "Select Payload", Filter = "All Files|*.*" };
        if (dlg.ShowDialog() == true)
        {
            _inputPath = dlg.FileName;
            _inputBox.Text = _inputPath;
            var name = IOPath.GetFileName(_inputPath);
            long size = 0;
            try { size = new FileInfo(_inputPath).Length; } catch { }
            var kb = Math.Max(1, (int)(size / 1024));

            var egg = CheckEasterEgg(name);
            _inputMeta = egg ?? $"{name}  ·  {kb:N0} KB  ·  crc32 {Crc32(name)}";
            _metaText.Text = _inputMeta;

            if (!_nameTouched)
            {
                _outputName = name;
                _outputNameBox.TextChanged -= OnOutputNameChanged;
                _outputNameBox.Text = _outputName;
                _outputNameBox.TextChanged += OnOutputNameChanged;
            }
            UpdateStatus("payload loaded");
        }
    }

    void OnPickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Select Output Folder" };
        if (dlg.ShowDialog() == true)
        {
            _outputDir = dlg.FolderName;
            _outputDirBox.Text = _outputDir;
        }
    }

    void OnInputPathChanged(object sender, TextChangedEventArgs e)
    {
        _inputPath = _inputBox.Text;
        var name = IOPath.GetFileName(_inputPath);
        var egg = CheckEasterEgg(name);
        _inputMeta = egg ?? (string.IsNullOrEmpty(name) ? "no payload loaded" : $"{name}  ·  path entered manually");
        _metaText.Text = _inputMeta;

        if (!_nameTouched)
        {
            _outputName = name;
            _outputNameBox.TextChanged -= OnOutputNameChanged;
            _outputNameBox.Text = _outputName;
            _outputNameBox.TextChanged += OnOutputNameChanged;
        }
    }

    void OnOutputNameChanged(object sender, TextChangedEventArgs e)
    {
        _outputName = _outputNameBox.Text;
        if (!_nameTouched)
        {
            _nameTouched = true;
            if (_nameSourceBadge != null) _nameSourceBadge.Text = "manual — locked";
        }
    }

    void OnResync()
    {
        _nameTouched = false;
        _outputName = IOPath.GetFileName(_inputPath);
        _outputNameBox.TextChanged -= OnOutputNameChanged;
        _outputNameBox.Text = _outputName;
        _outputNameBox.TextChanged += OnOutputNameChanged;
        if (_nameSourceBadge != null) _nameSourceBadge.Text = "auto";
    }

    // ========== GENERATE ==========

    void OnExploit()
    {
        if (string.IsNullOrEmpty(_inputPath))
        {
            UpdateStatus("! select a payload first");
            return;
        }
        if (string.IsNullOrEmpty(_outputDir))
        {
            UpdateStatus("! select an output folder");
            return;
        }

        var file = IOPath.GetFileName(_inputPath);
        var dir = _outputDir.EndsWith('\\') ? _outputDir : _outputDir + "\\";
        var outName = string.IsNullOrEmpty(_outputName) ? "out.bin" : _outputName;
        var outPath = dir + outName;

        _genDone = false;
        _genCts = new CancellationTokenSource();
        UpdateStatus("running PoC…");
        ShowGenerateModal(outPath);

        // Append recap lines immediately
        foreach (var line in GenerateOutput.BuildScript(file, outPath))
            AppendGenLine(line);

        StartExploitRun(_inputPath, outPath);
    }

    void ShowGenerateModal(string outPath)
    {
        HidePlaceholders();
        _genOverlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(199, 2, 6, 4)) };

        var dialogContent = new StackPanel();

        // Caption
        var caption = new Grid { Height = 26, Background = MakeCaptionGradient() };
        var captionLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        captionLeft.Children.Add(new Border
        {
            Width = 10, Height = 10, Background = B(CPrimaryGreen),
            BorderBrush = B(CDeskBg), BorderThickness = new Thickness(2),
            Margin = new Thickness(0, 0, 8, 0),
        });
        _genTitleText = new TextBlock
        {
            Text = "PoC RUNNING — TRUST CHAIN ANALYSIS…",
            FontFamily = UiFont, FontSize = 11,
            Foreground = B(C("#bff5da")),
            VerticalAlignment = VerticalAlignment.Center,
        };
        captionLeft.Children.Add(_genTitleText);
        caption.Children.Add(captionLeft);

        var closeBtn = MakeCaptionButton("×", C("#5c2038"), C("#a3667f"), CPinkLight, CloseGenerateModal);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Margin = new Thickness(0, 0, 4, 0);
        caption.Children.Add(closeBtn);
        dialogContent.Children.Add(caption);

        // Log area
        _genLogScroll = new ScrollViewer
        {
            Height = 340,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(14, 12, 14, 12),
            Template = CreateRetroScrollViewerTemplate(),
        };
        _genLogPanel = new StackPanel();
        _genLogScroll.Content = _genLogPanel;

        var logContent = new Border { Background = B(C("#040706")), Child = _genLogScroll };
        var logFrame = MakeInsetBevel(logContent, CBevelDark1, CBevelDark3, new Thickness(2, 2, 2, 2));
        logFrame.Margin = new Thickness(8);
        dialogContent.Children.Add(logFrame);

        // Progress row
        var progressRow = new Grid { Margin = new Thickness(10, 0, 10, 12) };
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        progressRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _genProgressBar = new Border
        {
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = B(CPrimaryGreen),
        };
        var progressContent = new Border
        {
            Height = 18, Background = B(CFieldBg),
            Padding = new Thickness(2),
            Child = _genProgressBar,
        };
        _genProgressFrame = progressContent;
        var progressFrame = MakeInsetBevel(progressContent, CBevelDark1, CBevelDark3, new Thickness(2, 2, 2, 2));
        Grid.SetColumn(progressFrame, 0);
        progressRow.Children.Add(progressFrame);

        _genPctText = new TextBlock
        {
            Text = "0%", FontFamily = TerminalFont, FontSize = 15,
            Foreground = B(CPrimaryGreen), TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_genPctText, 1);
        progressRow.Children.Add(_genPctText);

        _genActionBtn = MakeThemedButton("ABORT", 80, OnGenActionBtn);
        _genActionBtn.Margin = new Thickness(12, 0, 0, 0);
        Grid.SetColumn(_genActionBtn, 2);
        progressRow.Children.Add(_genActionBtn);

        dialogContent.Children.Add(progressRow);

        var dialogBg = new Border { Width = 720, Background = B(CWindowFace), Child = dialogContent };
        var dialog = MakeRaisedBevel(dialogBg, C("#4a5a56"), CBevelDark1, new Thickness(2, 2, 2, 2));
        dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.VerticalAlignment = VerticalAlignment.Center;

        _genOverlay.Children.Add(dialog);
        _rootGrid.Children.Add(_genOverlay);

    }

    void AppendGenLine(ConsoleLine line)
    {
        var tb = new TextBlock
        {
            Text = line.Text,
            FontFamily = TerminalFont, FontSize = 15,
            Foreground = B(GetLineColor(line.Color)),
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 21,
        };
        _genLogPanel!.Children.Add(tb);
        _genLogScroll!.ScrollToEnd();

        if (line.Progress.HasValue && _genProgressFrame != null)
        {
            var pct = line.Progress.Value;
            _genPctText!.Text = $"{pct}%";
            double maxWidth = _genProgressFrame.ActualWidth - 4;
            if (maxWidth > 0)
                _genProgressBar!.Width = maxWidth * pct / 100.0;
        }
    }

    void MarkExploitDone()
    {
        _genDone = true;
        if (_genTitleText != null) _genTitleText.Text = "PoC RUN COMPLETE";
        if (_genActionBtn != null) UpdateButtonText(_genActionBtn, "CLOSE");
        var dir = _outputDir.EndsWith('\\') ? _outputDir : _outputDir + "\\";
        var outName = string.IsNullOrEmpty(_outputName) ? "out.bin" : _outputName;
        UpdateStatus($"done → {dir}{outName}");
    }

    void StartExploitRun(string inputPath, string outPath)
    {
        var cts = _genCts!;
        _genThread = new Thread(() =>
        {
            GenerateOutput.RunExploit(inputPath, outPath, line =>
            {
                Dispatcher.BeginInvoke(() => AppendGenLine(line));
            }, cts.Token);

            Dispatcher.BeginInvoke(MarkExploitDone);
        })
        { IsBackground = true };
        _genThread.Start();
    }

    void OnGenActionBtn()
    {
        if (_genDone)
        {
            CloseGenerateModal();
        }
        else
        {
            _genCts?.Cancel();
            _genDone = true;
            _genLogPanel!.Children.Add(new TextBlock
            {
                Text = "!! ABORTED BY OPERATOR",
                FontFamily = TerminalFont, FontSize = 15,
                Foreground = B(CAccentPink), MinHeight = 21,
            });
            _genLogScroll!.ScrollToEnd();
            _genTitleText!.Text = "PoC RUN COMPLETE";
            UpdateButtonText(_genActionBtn!, "CLOSE");
            UpdateStatus("aborted");
        }
    }

    void CloseGenerateModal()
    {
        _genCts?.Cancel();
        if (_genOverlay != null) _rootGrid.Children.Remove(_genOverlay);
        _genOverlay = null;
        ShowPlaceholders();
    }

    // ========== HELP MODAL ==========

    void ShowHelpModal()
    {
        HidePlaceholders();
        _helpOverlay = new Grid { Background = new SolidColorBrush(Color.FromArgb(184, 2, 6, 4)) };
        _helpOverlay.MouseLeftButtonUp += (_, _) => CloseHelpModal();

        var dialogStack = new StackPanel();

        // Caption
        var caption = new Grid { Height = 24, Background = MakeCaptionGradient() };
        caption.Children.Add(new TextBlock
        {
            Text = "ABOUT / HOW TO USE",
            FontFamily = UiFont, FontSize = 11,
            Foreground = B(C("#bff5da")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(7, 0, 0, 0),
        });
        var closeBtn = MakeCaptionButton("×", C("#5c2038"), C("#a3667f"), CPinkLight, CloseHelpModal);
        closeBtn.HorizontalAlignment = HorizontalAlignment.Right;
        closeBtn.Margin = new Thickness(0, 0, 4, 0);
        caption.Children.Add(closeBtn);
        dialogStack.Children.Add(caption);

        // Body (in a ScrollViewer in case content is tall)
        var body = new StackPanel { Margin = new Thickness(20, 18, 20, 20) };

        body.Children.Add(new TextBlock
        {
            Text = AppConfig.AboutTitle,
            FontFamily = DisplayFont, FontSize = 15,
            Foreground = B(CPrimaryGreen),
            Margin = new Thickness(0, 0, 0, 14),
        });

        // WHAT IT DOES
        body.Children.Add(MakeSectionHeader("WHAT IT DOES", CPrimaryGreen));
        var whatText = new TextBlock
        {
            FontFamily = UiFont, FontSize = 13,
            Foreground = B(C("#a8c9bb")),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20.8,
            Margin = new Thickness(0, 0, 0, 16),
        };
        whatText.Inlines.Add(AppConfig.WhatItDoes);
        body.Children.Add(whatText);

        // HOW TO USE
        body.Children.Add(MakeSectionHeader("HOW TO USE", CPrimaryGreen));
        var howList = new StackPanel { Margin = new Thickness(20, 0, 0, 16) };
        string[] steps =
        [
            "Step 1 -> Set the payload by either typing a path or hitting [FILE...]",
            "Step 2 -> Choose what folder the file will be written to. Again you can either type or click the [FOLDER...] button to choose",
            "Step 3 -> Choose what the output file name will be. It auto-fills from the payload name. Edit it and it stays yours. Picking a new payload will not clobber it. RE-SYNC hands control back to auto.",
        ];
        for (int i = 0; i < steps.Length; i++)
        {
            howList.Children.Add(new TextBlock
            {
                Text = $"{i + 1}. {steps[i]}",
                FontFamily = UiFont, FontSize = 13,
                Foreground = B(CBodyText),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20.8,
                Margin = new Thickness(0, 0, 0, 7),
            });
        }
        body.Children.Add(howList);

        // Audio note
        var noteStack = new StackPanel();
        noteStack.Children.Add(new TextBlock
        {
            Text = "Music: chip-tune song by Space_Cat. Kill it top-right, or ride the volume slider.",
            FontFamily = TerminalFont, FontSize = 13,
            Foreground = B(CGreenMid),
            TextWrapping = TextWrapping.Wrap,
        });
        var notePadding = new Border { Background = B(CPanelInset), Padding = new Thickness(12, 10, 12, 10), Child = noteStack };
        body.Children.Add(MakeInsetBevel(notePadding, CBevelDark1, CBevelLight1, new Thickness(1, 1, 1, 1)));

        // GREETZ
        body.Children.Add(MakeSectionHeader("GREETZ", CAccentPink, new Thickness(0, 16, 0, 8)));
        var greetzGrid = new Grid();
        greetzGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        greetzGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        greetzGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (int gi = 0; gi < AppConfig.Greetz.Length; gi++)
        {
            var (name, reason) = AppConfig.Greetz[gi];
            greetzGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = name, FontFamily = TerminalFont, FontSize = 16,
                Foreground = B(CPrimaryGreen), Margin = new Thickness(0, 0, 0, 5),
            };
            Grid.SetRow(nameBlock, gi);
            Grid.SetColumn(nameBlock, 0);
            greetzGrid.Children.Add(nameBlock);

            var reasonBlock = new TextBlock
            {
                Text = reason, FontFamily = TerminalFont, FontSize = 16,
                Foreground = B(CGreenMid),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(reasonBlock, gi);
            Grid.SetColumn(reasonBlock, 2);
            greetzGrid.Children.Add(reasonBlock);
        }
        var greetzStack = new StackPanel();
        greetzStack.Children.Add(greetzGrid);
        greetzStack.Children.Add(new TextBlock
        {
            Text = AppConfig.GreetzClosing,
            FontFamily = TerminalFont, FontSize = 15,
            Foreground = B(CAccentPink),
            Margin = new Thickness(0, 9, 0, 0),
        });
        var greetzPadding = new Border { Background = B(CPanelInset), Padding = new Thickness(13, 11, 13, 11), Child = greetzStack };
        body.Children.Add(MakeInsetBevel(greetzPadding, CBevelDark1, CBevelLight1, new Thickness(1, 1, 1, 1)));

        // OK button
        var okBtn = MakeThemedButton("OK", 80, CloseHelpModal);
        okBtn.HorizontalAlignment = HorizontalAlignment.Right;
        okBtn.Margin = new Thickness(0, 16, 0, 0);
        body.Children.Add(okBtn);

        var scrollViewer = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 650,
            Template = CreateRetroScrollViewerTemplate(),
        };
        dialogStack.Children.Add(scrollViewer);

        // Opaque dialog background
        var dialogBg = new Border { Width = 520, Background = B(CWindowFace), Child = dialogStack };
        var dialog = MakeRaisedBevel(dialogBg, C("#4a5a56"), CBevelDark1, new Thickness(2, 2, 2, 2));
        dialog.HorizontalAlignment = HorizontalAlignment.Center;
        dialog.VerticalAlignment = VerticalAlignment.Center;
        dialog.MouseLeftButtonUp += (_, e) => e.Handled = true; // stopPropagation

        _helpOverlay.Children.Add(dialog);
        _rootGrid.Children.Add(_helpOverlay);
    }

    void CloseHelpModal()
    {
        if (_helpOverlay != null) _rootGrid.Children.Remove(_helpOverlay);
        _helpOverlay = null;
        ShowPlaceholders();
    }

    StackPanel MakeSectionHeader(string text, Color color, Thickness? margin = null)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = margin ?? new Thickness(0, 0, 0, 8),
        };
        row.Children.Add(new TextBlock
        {
            Text = text, FontFamily = DisplayFont, FontSize = 13, FontWeight = FontWeights.Bold,
            Foreground = B(color), Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new Border
        {
            Height = 1, Width = 200, VerticalAlignment = VerticalAlignment.Center,
            Background = new LinearGradientBrush(
                color == CAccentPink ? C("#3a2431") : C("#2b3a35"),
                Colors.Transparent, 0),
        });
        return row;
    }

    static ControlTemplate CreateRetroScrollViewerTemplate()
    {
        var xaml = @"
        <ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
                         xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
                         TargetType='ScrollViewer'>
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width='*'/>
                    <ColumnDefinition Width='10'/>
                </Grid.ColumnDefinitions>
                <ScrollContentPresenter Grid.Column='0'/>
                <ScrollBar x:Name='PART_VerticalScrollBar' Grid.Column='1'
                           Orientation='Vertical' Width='10'
                           Maximum='{TemplateBinding ScrollableHeight}'
                           Value='{TemplateBinding VerticalOffset}'
                           ViewportSize='{TemplateBinding ViewportHeight}'
                           Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'>
                    <ScrollBar.Style>
                        <Style TargetType='ScrollBar'>
                            <Setter Property='Template'>
                                <Setter.Value>
                                    <ControlTemplate TargetType='ScrollBar'>
                                        <Border Background='#0a1612' BorderBrush='#1a2e27' BorderThickness='1,0,0,0'>
                                            <Track x:Name='PART_Track' IsDirectionReversed='True'>
                                                <Track.Thumb>
                                                    <Thumb>
                                                        <Thumb.Template>
                                                            <ControlTemplate TargetType='Thumb'>
                                                                <Border x:Name='thumbBd' Background='#2b5a42'
                                                                        BorderBrush='#1a2e27' BorderThickness='1'
                                                                        Margin='1'>
                                                                </Border>
                                                                <ControlTemplate.Triggers>
                                                                    <Trigger Property='IsMouseOver' Value='True'>
                                                                        <Setter TargetName='thumbBd' Property='Background' Value='#3dff9e'/>
                                                                        <Setter TargetName='thumbBd' Property='BorderBrush' Value='#3dff9e'/>
                                                                    </Trigger>
                                                                    <Trigger Property='IsDragging' Value='True'>
                                                                        <Setter TargetName='thumbBd' Property='Background' Value='#7dffc2'/>
                                                                    </Trigger>
                                                                </ControlTemplate.Triggers>
                                                            </ControlTemplate>
                                                        </Thumb.Template>
                                                    </Thumb>
                                                </Track.Thumb>
                                                <Track.DecreaseRepeatButton>
                                                    <RepeatButton Command='ScrollBar.PageUpCommand' Opacity='0' Height='0'/>
                                                </Track.DecreaseRepeatButton>
                                                <Track.IncreaseRepeatButton>
                                                    <RepeatButton Command='ScrollBar.PageDownCommand' Opacity='0' Height='0'/>
                                                </Track.IncreaseRepeatButton>
                                            </Track>
                                        </Border>
                                    </ControlTemplate>
                                </Setter.Value>
                            </Setter>
                        </Style>
                    </ScrollBar.Style>
                </ScrollBar>
            </Grid>
        </ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // ========== HELPERS ==========

    Color GetLineColor(LineColor lc) => lc switch
    {
        LineColor.Bright => CGreenLight,
        LineColor.Dim => CGreenDim,
        LineColor.Green => CPrimaryGreen,
        LineColor.Warning => C("#ffd166"),
        LineColor.Pink => CAccentPink,
        _ => CGreenMid,
    };

    void UpdateStatus(string s)
    {
        _status = s;
        if (_statusText != null) _statusText.Text = s;
    }

    string? CheckEasterEgg(string basename)
    {
        if (string.IsNullOrEmpty(basename)) return null;
        var lower = basename.Trim().ToLowerInvariant();
        var stem = IOPath.GetFileNameWithoutExtension(lower);

        foreach (var (pattern, exact, msg) in AppConfig.MagicFilenames)
        {
            if (exact && lower == pattern.ToLowerInvariant()) return msg;
            if (!exact && stem == pattern.ToLowerInvariant()) return msg;
        }
        return null;
    }

    static string Crc32(string s)
    {
        uint h = 0xffffffff;
        foreach (char c in s)
        {
            h ^= c;
            for (int k = 0; k < 8; k++)
                h = (h >> 1) ^ (0xEDB88320u & (uint)(-(int)(h & 1)));
        }
        return (h ^ 0xffffffff).ToString("X8");
    }

    Button MakeThemedButton(string text, double width, Action onClick)
    {
        var content = new Grid { Background = B(CButtonFace) };
        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 5),
        };
        var icon = MakeButtonIcon(text);
        if (icon != null) btnPanel.Children.Add(icon);
        btnPanel.Children.Add(new TextBlock
        {
            Text = text, FontFamily = UiFont, FontSize = 11, FontWeight = FontWeights.Bold,
            Foreground = B(CGreenLight),
            VerticalAlignment = VerticalAlignment.Center,
        });
        content.Children.Add(btnPanel);

        var bevel = MakeRaisedBevel(content, CBevelLight2, CBevelDark2, new Thickness(2, 2, 2, 2));
        bevel.Width = width;
        bevel.Cursor = Cursors.Hand;
        bevel.MouseEnter += (_, _) => content.Background = B(CButtonHover);
        bevel.MouseLeave += (_, _) => content.Background = B(CButtonFace);
        bevel.MouseLeftButtonUp += (_, _) => onClick();

        // Wrap in a Button so callers that set .Content still work
        var btn = new Button
        {
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)),
            },
            Content = bevel,
            Width = width,
            Cursor = Cursors.Hand,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    static Canvas? MakeButtonIcon(string text)
    {
        if (text.StartsWith("FILE", StringComparison.OrdinalIgnoreCase))
            return MakeDocumentIcon();
        if (text.StartsWith("FOLDER", StringComparison.OrdinalIgnoreCase))
            return MakeFolderIcon();
        return null;
    }

    static Canvas MakeDocumentIcon()
    {
        var c = new Canvas { Width = 14, Height = 18, Margin = new Thickness(0, 0, 6, 0), SnapsToDevicePixels = true };
        // Sheet body
        c.Children.Add(Px(0, 0, 14, 18, "#0d1b16")); // outline
        c.Children.Add(Px(2, 2, 10, 14, "#e9e7d6")); // fill
        // Cut corner top-right (triangle area = button face color)
        c.Children.Add(Px(8, 0, 6, 6, "#26332f")); // corner bg
        c.Children.Add(Px(8, 6, 6, 2, "#0d1b16")); // fold line horizontal
        c.Children.Add(Px(8, 0, 2, 6, "#0d1b16")); // fold line vertical
        // Text lines
        c.Children.Add(Px(3, 9, 8, 2, "#6b8f80"));
        c.Children.Add(Px(3, 12, 8, 2, "#6b8f80"));
        c.Children.Add(Px(3, 15, 5, 2, "#6b8f80"));
        return c;
    }

    static Canvas MakeFolderIcon()
    {
        var c = new Canvas { Width = 20, Height = 16, Margin = new Thickness(0, 0, 6, 0), SnapsToDevicePixels = true };
        // Tab
        c.Children.Add(Px(0, 0, 9, 5, "#3b2f0c")); // tab outline
        c.Children.Add(Px(2, 2, 5, 3, "#e0bb4e")); // tab fill
        // Body
        c.Children.Add(Px(0, 3, 20, 13, "#3b2f0c")); // body outline
        c.Children.Add(Px(2, 5, 16, 9, "#f2d268")); // body fill
        // Highlight
        c.Children.Add(Px(2, 5, 16, 2, "#fbeaa6"));
        return c;
    }

    static System.Windows.Shapes.Rectangle Px(double x, double y, double w, double h, string color)
    {
        var r = new System.Windows.Shapes.Rectangle { Width = w, Height = h, Fill = B(color) };
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        return r;
    }

    static void UpdateButtonText(Button btn, string text)
    {
        if (btn.Content is Border bevel)
        {
            var inner = (Border)bevel.Child!;
            var grid = (Grid)inner.Child!;
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb) { tb.Text = text; return; }
                if (child is StackPanel sp)
                    foreach (var sc in sp.Children)
                        if (sc is TextBlock stb) { stb.Text = text; return; }
            }
        }
    }

    static LinearGradientBrush MakeCaptionGradient()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = { new(CCaptionTop, 0), new(CCaptionMid, 0.55), new(CCaptionBot, 1) },
        };
        brush.Freeze();
        return brush;
    }

    static LinearGradientBrush MakeGenerateGradient(bool hover)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
            GradientStops = hover
                ? new GradientStopCollection { new(C("#9affd2"), 0), new(C("#2bd489"), 0.6), new(C("#17ab6a"), 1) }
                : new GradientStopCollection { new(C("#7dffc2"), 0), new(C("#22c07a"), 0.6), new(C("#13985e"), 1) },
        };
        brush.Freeze();
        return brush;
    }

    static DrawingBrush CreateScanlineBrush(double opacity = 0.055, Color? baseColor = null)
    {
        var bc = baseColor ?? Color.FromRgb(61, 255, 158);
        var lineColor = new SolidColorBrush(Color.FromArgb((byte)(opacity * 255), bc.R, bc.G, bc.B));
        lineColor.Freeze();
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(lineColor, null,
            new RectangleGeometry(new Rect(0, 0, 100, 2))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 100, 4),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 100, 4),
            ViewboxUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    static void AddCrtOverlay(Grid target, double scanlineOpacity, bool addVignette = true)
    {
        // Scanlines — horizontal 2px on / 2px off stripes
        target.Children.Add(new Rectangle
        {
            Fill = CreateScanlineBrush(scanlineOpacity),
            IsHitTestVisible = false,
        });

        if (!addVignette) return;

        // CRT edge vignette — darkened corners/edges for barrel-distortion look
        var vignette = new Rectangle
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.75, RadiusY = 0.75,
                GradientStops =
                {
                    new(Colors.Transparent, 0),
                    new(Colors.Transparent, 0.6),
                    new(Color.FromArgb(40, 0, 0, 0), 0.8),
                    new(Color.FromArgb(100, 0, 0, 0), 1.0),
                },
            },
        };
        target.Children.Add(vignette);

        // Top/bottom edge highlight — simulates CRT screen curvature reflection
        target.Children.Add(new Rectangle
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Top,
            Fill = new SolidColorBrush(Color.FromArgb(18, 61, 255, 158)),
            IsHitTestVisible = false,
        });
        target.Children.Add(new Rectangle
        {
            Height = 1, VerticalAlignment = VerticalAlignment.Bottom,
            Fill = new SolidColorBrush(Color.FromArgb(12, 61, 255, 158)),
            IsHitTestVisible = false,
        });
    }

    static DrawingBrush CreateGridBrush(Color? lineColor = null)
    {
        var group = new DrawingGroup();
        var lc = lineColor ?? Color.FromRgb(61, 255, 158);
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(18, lc.R, lc.G, lc.B)), 1);
        pen.Freeze();
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 0), new Point(32, 0))));
        group.Children.Add(new GeometryDrawing(null, pen, new LineGeometry(new Point(0, 0), new Point(0, 32))));
        return new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 32, 32),
            ViewportUnits = BrushMappingMode.Absolute,
        };
    }

    static DrawingBrush CreateDiagonalStripeBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(B("#0e1514"), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(B("#141c1a"), null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(B("#141c1a"), null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            Transform = new RotateTransform(45),
        };
        brush.Freeze();
        return brush;
    }

    readonly List<PlaceholderAdorner> _placeholderAdorners = [];

    void SetPlaceholder(TextBox textBox, string placeholder)
    {
        textBox.Loaded += (_, _) =>
        {
            var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(textBox);
            if (layer != null)
            {
                var adorner = new PlaceholderAdorner(textBox, placeholder);
                layer.Add(adorner);
                _placeholderAdorners.Add(adorner);
            }
        };
    }

    void HidePlaceholders()
    {
        foreach (var a in _placeholderAdorners) a.Visibility = Visibility.Collapsed;
    }

    void ShowPlaceholders()
    {
        foreach (var a in _placeholderAdorners) a.Visibility = Visibility.Visible;
    }

    static Color HslToColor(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Clamp((r + m) * 255, 0, 255),
            (byte)Math.Clamp((g + m) * 255, 0, 255),
            (byte)Math.Clamp((b + m) * 255, 0, 255));
    }
}

internal class PlaceholderAdorner : System.Windows.Documents.Adorner
{
    private readonly string _text;
    public PlaceholderAdorner(TextBox adornedElement, string text) : base(adornedElement)
    {
        _text = text;
        IsHitTestVisible = false;
        adornedElement.TextChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var tb = (TextBox)AdornedElement;
        if (!string.IsNullOrEmpty(tb.Text)) return;

        var ft = new FormattedText(
            _text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            13,
            new SolidColorBrush(Color.FromArgb(100, 78, 138, 112)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        drawingContext.DrawText(ft, new Point(tb.Padding.Left + 2, tb.Padding.Top + 2));
    }
}
