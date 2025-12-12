using Microsoft.Win32;          // OpenFileDialog
using System;
using System.Collections.Generic;
using System.Diagnostics;       // Process.Start
using System.IO;
using System.Text;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;

namespace FlowChart
{
    public partial class MainWindow : Window
    {
        
        
            private ProjectData? currentProject;
            private Ellipse? connectionStartConnector = null;
            private Line? connectionPreviewLine = null;
            private Ellipse? currentHoverConnector = null;
            private Dictionary<string, ProjectData> allProjects = new();
            private static readonly string SaveFilePath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FlowChartApp",
        "projects.json");

        private Line? selectedConnectionLine = null;
            private BlockData? selectedBlock = null;
            private Grid? draggingPanel = null;
            private Point dragStartPos;
            private bool isDragging = false;
            private ConnectorTag? startConnector = null;
            private bool isConnecting = false;                 // 接続処理中フラグ
            private List<Ellipse> allConnectorEllipses = new(); // 全コネクタ参照をキャッシュ（高速ヒットテスト用）
            private bool isUpdatingConnections = false; // 接続更新中フラグ（再帰防止用）
            private readonly object saveLock = new();
        private bool isSaving = false;
        private string? currentProjectName;

        public MainWindow()
            {
                InitializeComponent();
                WindowState = WindowState.Maximized;
            }
           

            // =================== コネクタドラッグ接続 ===================
            private void Connector_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
            if (sender is not Ellipse connector) return;
            if (connector.Tag is not ConnectorTag tag) return; // ←追加（どのブロックに属するか取得）

            var block = FindBlockById(tag.BlockId);            
            if (block == null) return;

            connectionStartConnector = connector;
            connectionPreviewLine = new Line
            {
                Stroke = Brushes.Orange,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 3, 3 },
                IsHitTestVisible = false
            };

            Point start = GetConnectorCanvasPosition(connector,block);
            if (start == null) return;
            connectionPreviewLine.X1 = start.X;
            connectionPreviewLine.Y1 = start.Y;
            connectionPreviewLine.X2 = start.X;
            connectionPreviewLine.Y2 = start.Y;

            FlowCanvas.Children.Add(connectionPreviewLine);
            FlowCanvas.CaptureMouse();

            FlowCanvas.MouseMove += FlowCanvas_MouseMove_WhileConnecting;
            FlowCanvas.MouseLeftButtonUp += FlowCanvas_MouseLeftButtonUp_WhileConnecting;

            e.Handled = true;
        }

            //private void Connector_MouseMove(object sender, MouseEventArgs e)
            //{
                //if (connectionPreviewLine == null || connectionStartConnector == null) return;
                //var pos = e.GetPosition(FlowCanvas);
                //connectionPreviewLine.X2 = pos.X;
                //connectionPreviewLine.Y2 = pos.Y;

                // Hover中の黒ぽち検知
                //Ellipse? hover = GetConnectorAtPosition(pos);
                //if (hover != currentHoverConnector)
                //{
                  //  if (currentHoverConnector != null)
                    //    currentHoverConnector.Fill = Brushes.Black;
                    //if (hover != null)
                      //  hover.Fill = Brushes.Orange;
                   // currentHoverConnector = hover;
                //}
            //}

            private void Connector_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
            Debug.WriteLine("Connector_MouseUp triggered");
            if (connectionStartConnector == null || connectionPreviewLine == null)
                    return;

            //接続終了時にイベントを解除
            FlowCanvas.MouseMove -= FlowCanvas_MouseMove_WhileConnecting;
            FlowCanvas.MouseLeftButtonUp -= FlowCanvas_MouseLeftButtonUp_WhileConnecting;
            FlowCanvas.ReleaseMouseCapture();

            Mouse.Capture(null);

                var endPos = e.GetPosition(FlowCanvas);
                Ellipse? targetConnector = GetConnectorAtPosition(endPos);

                // cleanup preview line
                FlowCanvas.Children.Remove(connectionPreviewLine);
                connectionPreviewLine = null;

                if (targetConnector == null || targetConnector == connectionStartConnector)
                {
                    connectionStartConnector.Fill = Brushes.Black;
                    connectionStartConnector = null;
                    if (currentHoverConnector != null)
                        currentHoverConnector.Fill = Brushes.Black;
                    return;
                }

                // 接続確定
                CreateConnection(connectionStartConnector, targetConnector);

                connectionStartConnector.Fill = Brushes.Black;
                connectionStartConnector = null;
                if (currentHoverConnector != null)
                {
                    currentHoverConnector.Fill = Brushes.Black;
                    currentHoverConnector = null;
                }
                SaveProjectsToFile();
            }

            private Ellipse? GetConnectorAtPosition(Point pos)
            {
                // キャッシュ走査：すべてのコネクタ Ellipse を高速にチェック
                // 参照は DrawBlock で allConnectorEllipses に登録してある前提
                foreach (var el in allConnectorEllipses)
                {
                    // 親パネルを取得して位置を計算
                    if (el.Parent is not Grid panel) continue;
                    double panelLeft = Canvas.GetLeft(panel);
                    double panelTop = Canvas.GetTop(panel);
                    if (double.IsNaN(panelLeft)) panelLeft = 0;
                    if (double.IsNaN(panelTop)) panelTop = 0;

                    var m = el.Margin;
                    double left = panelLeft + m.Left;
                    double top = panelTop + m.Top;
                    var rect = new Rect(left, top, el.Width, el.Height);
                    if (rect.Contains(pos)) return el;
                }
                return null;

            }

            private ConnectionData? CreateConnection(Ellipse start, Ellipse end)
            {
                if (currentProject == null) return null;
                if (start.Tag is not ConnectorTag sTag || end.Tag is not ConnectorTag eTag) 
                    return null;

                var startBlock = FindBlockById(sTag.BlockId);
                var endBlock = FindBlockById(eTag.BlockId);
                if (startBlock == null || endBlock == null) return null;

                Point s = GetConnectorCanvasPosition(start,startBlock);
                Point t = GetConnectorCanvasPosition(end, endBlock);

                var line = new Line
                {
                    X1 = s.X,
                    Y1 = s.Y,
                    X2 = t.X,
                    Y2 = t.Y,
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    IsHitTestVisible = true
                };
                

                var conn = new ConnectionData()
                {
                    FromBlockId = sTag.BlockId,
                    FromConnector = sTag.Name,
                    ToBlockId = eTag.BlockId,
                    ToConnector = eTag.Name,
                    X1 = s.X,
                    Y1 = s.Y,
                    X2 = t.X,
                    Y2 = t.Y,
                    LineRef = line
                };
                line.Tag = conn;
                line.MouseLeftButtonDown += Connection_MouseLeftButtonDown;
                FlowCanvas.Children.Add(line);
                currentProject.Connections.Add(conn);

                // 線の位置更新（即追従反映）
                UpdateConnectionsForBlock(startBlock);
                UpdateConnectionsForBlock(endBlock);

            return conn;
        }

            private void Connection_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                if (sender is not Line line || line.Tag is not ConnectionData conn) return;
                if (MessageBox.Show("この線を削除しますか？", "確認", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    FlowCanvas.Children.Remove(line);
                    currentProject?.Connections.Remove(conn);
                    SaveProjectsToFile();
                }
                

            }

        private void FlowCanvas_MouseMove_WhileConnecting(object sender, MouseEventArgs e)
        {
            if (connectionPreviewLine == null && connectionStartConnector == null) return;

            // 始点：接続開始したコネクタのキャンバス座標を使う
            ConnectorTag? tag = connectionStartConnector.Tag as ConnectorTag;
            BlockData? startBlock = tag != null ? FindBlockById(tag.BlockId) : null;
            Point start = GetConnectorCanvasPosition(connectionStartConnector,startBlock);
            Point end = e.GetPosition(FlowCanvas);

                connectionPreviewLine.X1 = start.X;
                connectionPreviewLine.Y1 = start.Y;
                connectionPreviewLine.X2 = end.X;
                connectionPreviewLine.Y2 = end.Y;
            
        }
        private void FlowCanvas_MouseLeftButtonUp_WhileConnecting(object sender, MouseButtonEventArgs e)
        {
            if (connectionStartConnector == null || connectionPreviewLine == null)
            {
                CleanupConnectionPreview();
                return;
            }

            // マウス位置の下にあるコネクタを探す
            Point pos = e.GetPosition(FlowCanvas);
            Ellipse? endConnector = GetConnectorAtPosition(pos);

            //一時線削除
            if (FlowCanvas.Children.Contains(connectionPreviewLine))
                FlowCanvas.Children.Remove(connectionPreviewLine);
            connectionPreviewLine = null;

            if (endConnector == null || endConnector == connectionStartConnector)
            {
                connectionStartConnector.Fill = Brushes.Black;
                connectionStartConnector = null;
                CleanupConnectionPreview();
                return;
            }

            // 正常接続確定
            var newConn = CreateConnection(connectionStartConnector, endConnector);

            if (newConn != null)
            {
                // 即表示
                if (newConn.LineRef != null && !FlowCanvas.Children.Contains(newConn.LineRef))
                    FlowCanvas.Children.Add(newConn.LineRef);

                // 線の座標を最新化（描画ズレ対策）
                var startBlock = FindBlockById(newConn.FromBlockId);
                if (startBlock != null)
                    UpdateConnectionsForBlock(startBlock);
            }

            connectionStartConnector.Fill = Brushes.Black;
            connectionStartConnector = null;

            Dispatcher.BeginInvoke(new Action(() => SaveProjectsToFile()), System.Windows.Threading.DispatcherPriority.Background);

            // 後片付け
            FlowCanvas.MouseMove -= FlowCanvas_MouseMove_WhileConnecting;
            FlowCanvas.MouseLeftButtonUp -= FlowCanvas_MouseLeftButtonUp_WhileConnecting;
            if (Mouse.Captured == FlowCanvas) Mouse.Capture(null);
            CleanupConnectionPreview();
        }


        private void CleanupConnectionPreview()
        {
            if (connectionPreviewLine != null && FlowCanvas.Children.Contains(connectionPreviewLine))
            FlowCanvas.Children.Remove(connectionPreviewLine);
            connectionPreviewLine = null;
            // イベント解除
            FlowCanvas.MouseMove -= FlowCanvas_MouseMove_WhileConnecting;
            FlowCanvas.MouseLeftButtonUp -= FlowCanvas_MouseLeftButtonUp_WhileConnecting;

            // release capture
            if (Mouse.Captured == FlowCanvas) Mouse.Capture(null);
        }


        private (double X1, double Y1, double X2, double Y2) GetConnectorLinePosition(ConnectionData cd)
            {
                var sBlock = FindBlockById(cd.FromBlockId);
                var tBlock = FindBlockById(cd.ToBlockId);
                if (sBlock == null || tBlock == null) return (0, 0, 0, 0);
                var sConn = FindConnectorEllipse(sBlock, cd.FromConnector);
                var tConn = FindConnectorEllipse(tBlock, cd.ToConnector);
                if (sConn == null || tConn == null) return (0, 0, 0, 0);
                var sp = GetConnectorCanvasPosition(sConn,sBlock);
                var tp = GetConnectorCanvasPosition(tConn,tBlock);
                return (sp.X, sp.Y, tp.X, tp.Y);
            }

        // -------------------- Window Loaded --------------------
        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            LoadProjectsFromFile();

            // 初期スクロール：縦最上部・横中央
            MainScrollViewer.UpdateLayout();
            double centerX = (FlowCanvas.Width - MainScrollViewer.ViewportWidth) / 2;
            if (centerX < 0) centerX = 0;
            MainScrollViewer.ScrollToHorizontalOffset(centerX);
            MainScrollViewer.ScrollToVerticalOffset(0);
        }

        // -------------------- Project CRUD & Save/Load --------------------
        private void AddProject_Click(object sender, RoutedEventArgs e)
        {
            string name = ProjectNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name)) { MessageBox.Show("企画名を入力してください"); return; }
            if (allProjects.ContainsKey(name)) { MessageBox.Show("同名の企画があります"); return; }

            var proj = new ProjectData
            {
                Name = name,
                StartDate = StartDatePicker.SelectedDate ?? DateTime.Now,
                Author = AuthorBox.Text.Trim(),
                Blocks = new List<BlockData>(),
                Files = new List<string>(),
                Connections = new List<ConnectionData>()
            };

            allProjects[name] = proj;
            ProjectSelector.Items.Add(name);
            ProjectSelector.SelectedItem = name;

            // create folder for files
            Directory.CreateDirectory(GetProjectFilesFolder(name));

            SaveProjectsToFile();

            ProjectNameBox.Clear();
            AuthorBox.Clear();
            allConnectorEllipses.Clear();
            RedrawCanvas();

        }

        private void ProjectSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProjectSelector.SelectedItem == null) return;
            string name = ProjectSelector.SelectedItem.ToString() ?? "";
            if (!allProjects.TryGetValue(name, out var proj)) return;
            currentProject = proj;
            currentProjectName = name;
            AuthorDisplay.Text = $"管理者: {proj.Author}";
            RefreshFileList();
            RedrawCanvas();
        }

        private void RedrawCanvas()
        {
            if (currentProject == null) return;
            FlowCanvas.Children.Clear();

            foreach (var block in currentProject.Blocks)
                DrawBlock(block);

            foreach (var conn in currentProject.Connections)
            {
                var startBlock = FindBlockById(conn.FromBlockId);
                var endBlock = FindBlockById(conn.ToBlockId);
                if (startBlock == null || endBlock == null) continue;

                var startConn = FindConnectorEllipse(startBlock, conn.FromConnector);
                var endConn = FindConnectorEllipse(endBlock, conn.ToConnector);
                if (startConn == null || endConn == null) continue;

                Point s = GetConnectorCanvasPosition(startConn,startBlock);
                Point t = GetConnectorCanvasPosition(endConn,endBlock);

                var line = new Line
                {
                    X1 = s.X,
                    Y1 = s.Y,
                    X2 = t.X,
                    Y2 = t.Y,
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                };
                line.Tag = conn;
                conn.LineRef = line;
                line.MouseLeftButtonDown += Connection_MouseLeftButtonDown;

                FlowCanvas.Children.Add(line);
                
                conn.LineRef = line;
               
            }
            // ✅ UI レイアウト完了後に再更新を強制
            Dispatcher.BeginInvoke(new Action(() =>
            {
                UpdateAllConnections();
            }), System.Windows.Threading.DispatcherPriority.Loaded);

        }
        private void UpdateAllConnections()
        {
            if (currentProject == null) return;

            foreach (var conn in currentProject.Connections)
            {
                if (conn.LineRef == null) continue;

                var startBlock = FindBlockById(conn.FromBlockId);
                var endBlock = FindBlockById(conn.ToBlockId);
                if (startBlock == null || endBlock == null) continue;

                var startConn = FindConnectorEllipse(startBlock, conn.FromConnector);
                var endConn = FindConnectorEllipse(endBlock, conn.ToConnector);
                if (startConn == null || endConn == null) continue;

                Point s = GetConnectorCanvasPosition(startConn, startBlock);
                Point t = GetConnectorCanvasPosition(endConn, endBlock);

                conn.LineRef.X1 = s.X;
                conn.LineRef.Y1 = s.Y;
                conn.LineRef.X2 = t.X;
                conn.LineRef.Y2 = t.Y;
            }
        }


        private void RedrawAllConnections()
        {
            foreach (var conn in currentProject.Connections)
            {
                var startBlock = FindBlockById(conn.FromBlockId);
                var endBlock = FindBlockById(conn.ToBlockId);
                if (startBlock == null || endBlock == null) continue;

                var startConn = FindConnectorEllipse(startBlock, conn.FromConnector);
                var endConn = FindConnectorEllipse(endBlock, conn.ToConnector);
                if (startConn == null || endConn == null) continue;

                var line = new Line
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    IsHitTestVisible = false
                };
                FlowCanvas.Children.Add(line);
                conn.LineRef = line;

                Point s = GetConnectorCanvasPosition(startConn,startBlock);
                Point t = GetConnectorCanvasPosition(endConn,endBlock);
                line.X1 = s.X;
                line.Y1 = s.Y;
                line.X2 = t.X;
                line.Y2 = t.Y;
            }
        }



        private void DeleteProjectButton_Click(object sender, RoutedEventArgs e)
        {
            if (ProjectSelector.SelectedItem == null) return;
            string name = ProjectSelector.SelectedItem.ToString() ?? "";
            if (MessageBox.Show($"プロジェクト「{name}」を削除しますか？", "確認", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;

            // delete files folder (optional)
            string folder = GetProjectFilesFolder(name);
            if (Directory.Exists(folder))
            {
                try { Directory.Delete(folder, true); } catch { /*ignore*/ }
            }

            allProjects.Remove(name);
            ProjectSelector.Items.Remove(name);
            currentProject = null;
            FlowCanvas.Children.Clear();
            FileListBox.Items.Clear();
            SaveProjectsToFile();
            allConnectorEllipses.Clear();
            RedrawCanvas();

        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => SaveProjectsToFile();
        private void LoadButton_Click(object sender, RoutedEventArgs e) => LoadProjectsFromFile();

        private void SaveProjectsToFile()
        {
            try
            {
                if (currentProject != null && !string.IsNullOrEmpty(currentProject.Name))
                    allProjects[currentProject.Name] = currentProject;

                foreach (var proj in allProjects.Values)
                {
                    if (proj.Blocks == null) continue;
                    foreach (var b in proj.Blocks)
                    {
                        if (double.IsNaN(b.X) || double.IsInfinity(b.X)) b.X = 0;
                        if (double.IsNaN(b.Y) || double.IsInfinity(b.Y)) b.Y = 0;
                    }
                }

                var opts = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
                };

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SaveFilePath)!);

                string json = JsonSerializer.Serialize(allProjects, opts);
                File.WriteAllText(SaveFilePath, json);
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存に失敗しました: " + ex.Message);
            }
        }



        private void LoadProjectsFromFile()
        {
            try
            {
                if (!File.Exists(SaveFilePath))
                {
                    Debug.WriteLine("保存ファイルが見つかりません: " + SaveFilePath);
                    return;
                }

                string json = File.ReadAllText(SaveFilePath);
                allProjects = JsonSerializer.Deserialize<Dictionary<string, ProjectData>>(json) ?? new();

                ProjectSelector.Items.Clear();
                foreach (var name in allProjects.Keys)
                    ProjectSelector.Items.Add(name);

                if (ProjectSelector.Items.Count > 0)
                    ProjectSelector.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("データ読み込みに失敗しました: " + ex.Message);
            }
        }

        // -------------------- File attachments --------------------
        private void AddFile_Click(object sender, RoutedEventArgs e)
        {
            if (currentProject == null) { MessageBox.Show("企画を選択してください"); return; }

            var dlg = new OpenFileDialog() { Multiselect = false };
            if (dlg.ShowDialog() != true) return;
            string src = dlg.FileName;
            string folder = GetProjectFilesFolder(currentProject.Name);
            Directory.CreateDirectory(folder);
            string dst = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(src));
            File.Copy(src, dst, true);
            currentProject.Files.Add(System.IO.Path.GetFileName(src));
            RefreshFileList();
            SaveProjectsToFile();
            RebuildConnections();
        }

        private void RefreshFileList()
        {
            FileListBox.Items.Clear();
            if (currentProject == null) return;
            foreach (var f in currentProject.Files) FileListBox.Items.Add(f);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (currentProject == null) return;
            if (FileListBox.SelectedItem == null) { MessageBox.Show("開くファイルを選択してください"); return; }
            string filename = FileListBox.SelectedItem.ToString() ?? "";
            string path = System.IO.Path.Combine(GetProjectFilesFolder(currentProject.Name), filename);
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else MessageBox.Show("ファイルが見つかりません: " + path);
        }

        private void OpenDeleteFilesWindow_Click(object sender, RoutedEventArgs e)
        {
            if (currentProject == null) { MessageBox.Show("企画を選択してください"); return; }
            var win = new DeleteFilesWindow(currentProject);
            win.Owner = this;
            if (win.ShowDialog() == true)
            {
                RefreshFileList();
                SaveProjectsToFile();
                RebuildConnections();
            }
        }
        private void RebuildConnections()
        {
            foreach (var conn in currentProject.Connections)
            {
                var startBlock = FindBlockById(conn.FromBlockId);
                var endBlock = FindBlockById(conn.ToBlockId);
                if (startBlock == null || endBlock == null) continue;

                var startConn = FindConnectorEllipse(startBlock, conn.FromConnector);
                var endConn = FindConnectorEllipse(endBlock, conn.ToConnector);
                if (startConn == null || endConn == null) continue;

                Point s = GetConnectorCanvasPosition(startConn,startBlock);
                Point t = GetConnectorCanvasPosition(endConn,endBlock);

                Line line = new Line
                {
                    Stroke = Brushes.Black,
                    StrokeThickness = 2,
                    X1 = s.X,
                    Y1 = s.Y,
                    X2 = t.X,
                    Y2 = t.Y
                };

                FlowCanvas.Children.Add(line);
                conn.LineRef = line;
            }
        }


        // -------------------- Drag from tool palette --------------------
        private void BlockTemplate_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is Canvas toolCanvas && toolCanvas.Tag is string tag)
            {
                DragDrop.DoDragDrop(toolCanvas, tag, DragDropEffects.Copy);
            }
        }
        


        // -------------------- Drop on canvas -> create block --------------------
        private void FlowCanvas_Drop(object sender, DragEventArgs e)
        {
            if (currentProject == null) { MessageBox.Show("企画を選択してください"); return; }
            if (!e.Data.GetDataPresent(DataFormats.StringFormat) && !e.Data.GetDataPresent(DataFormats.Text)) return;

            string shape = (e.Data.GetData(DataFormats.StringFormat) ?? e.Data.GetData(DataFormats.Text))?.ToString() ?? "Rectangle";
            Point pos = e.GetPosition(FlowCanvas);

            // create block
            var block = new BlockData
            {
                Id = Guid.NewGuid().ToString(),
                Shape = shape,
                X = pos.X,
                Y = pos.Y,
                Summary = "",
                Memo = "",
                Status = "未着手"
            };

            currentProject.Blocks.Add(block);
            DrawBlock(block);
            SaveProjectsToFile();
        }

        // -------------------- Draw block (Grid panel with shape + text + connectors) --------------------
        private void DrawBlock(BlockData block)
        {
            // Panel
            double w = 120, h = 70;
            var panel = new Grid { Width = w, Height = h, Tag = block };
            block.PanelRef = panel;
            
            // shape element
            Shape shapeEl;
            switch (block.Shape)
            {
                case "Rectangle":
                    shapeEl = new Rectangle { Width = w, Height = h, RadiusX = 4, RadiusY = 4 };
                    break;
                case "Diamond":
                    var polyD = new Polygon { Points = new PointCollection { new(w / 2, 0), new(w, h / 2), new(w / 2, h), new(0, h / 2) } };
                    shapeEl = polyD;
                    break;
                case "Ellipse":
                    shapeEl = new Ellipse { Width = w, Height = h };
                    break;
                case "UpTriangle":
                    shapeEl = new Polygon { Points = new PointCollection { new(w / 2, 0), new(w, h), new(0, h) } };
                    break;
                case "DownTriangle":
                    shapeEl = new Polygon { Points = new PointCollection { new(0, 0), new(w, 0), new(w / 2, h) } };
                    break;
                default:
                    shapeEl = new Rectangle { Width = w, Height = h };
                    break;
            }

            shapeEl.Fill = GetStatusBrush(block.Status);
            shapeEl.Stroke = Brushes.Black;
            shapeEl.StrokeThickness = 2;

            // text
            var tb = new TextBlock
            {
                Text = block.Summary ?? "",
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            panel.Children.Add(shapeEl);
            panel.Children.Add(tb);

            // connectors (ellipses) -> positions depend on shape
            var connectors = CreateConnectorsForShape(block, w, h);
            foreach (var conn in connectors)
            {
                if (conn.UI != null)
                {
                    panel.Children.Add(conn.UI);
                    conn.UI.Tag = new ConnectorTag { BlockId = block.Id, Name = conn.Name, Allowed = conn.Allowed };
                    conn.UI.MouseLeftButtonDown += Connector_MouseLeftButtonDown;
                    allConnectorEllipses.Add(conn.UI);
                }
            }

            panel.MouseRightButtonUp += (s, e) =>
            {
                ShowBlockDeleteDialog(block, panel);
                e.Handled = true;
            };

            // wrap in a Canvas container (so we can position)
            FlowCanvas.Children.Add(panel);
            Canvas.SetLeft(panel, block.X);
            Canvas.SetTop(panel, block.Y);

            // events for dragging and selecting
            panel.MouseLeftButtonDown += Block_MouseLeftButtonDown;
            panel.MouseMove += Block_MouseMove;
            panel.MouseLeftButtonUp += Block_MouseLeftButtonUp;

            // store reference
            block.TextBlockRef = tb;
            block.Connectors = connectors;
        }

        // Connector model & factory
        private List<ConnectorInfo> CreateConnectorsForShape(BlockData block, double w, double h)
        {
            var result = new List<ConnectorInfo>();

            void AddConnector(string name, double left, double top, string allowed)
            {
                var e = new Ellipse { Width = 10, Height = 10, Fill = Brushes.Black, Stroke = Brushes.White, StrokeThickness = 1, Opacity = 0.95 };
                // position using Margin inside panel (Grid)
                e.HorizontalAlignment = HorizontalAlignment.Left;
                e.VerticalAlignment = VerticalAlignment.Top;
                e.Margin = new Thickness(left, top, 0, 0);
                result.Add(new ConnectorInfo { Name = name, UI = e, Allowed = allowed });
            }

            // Diamond & Triangles: all corners
            if (block.Shape == "Diamond")
            {
                AddConnector("corner1", w * 0.5 - 5, 0, "any");
                AddConnector("corner2", w - 10, h * 0.5 - 5, "any");
                AddConnector("corner3", w * 0.5 - 5, h - 10, "any");
                AddConnector("corner4", 0, h * 0.5 - 5, "any");
            }
            else if (block.Shape == "UpTriangle")
            {
                // triangle pointing up: vertices are (w/2,0), (w,h), (0,h)
                AddConnector("v_top", w * 0.5 - 5, 0 - 0 /*top*/, "any");
                AddConnector("v_right", w - 10, h - 10, "any");
                AddConnector("v_left", 0, h - 10, "any");
            }
            else if (block.Shape == "DownTriangle")
            {
                // triangle pointing down: vertices are (0,0), (w,0), (w/2,h)
                AddConnector("v_top_left", 0, 0, "any");
                AddConnector("v_top_right", w - 10, 0, "any");
                AddConnector("v_bottom", w * 0.5 - 5, h - 10, "any");
            }
            else if (block.Shape == "Rectangle")
            {
                // two horizontal connectors (left+right mid)
                AddConnector("h_left", 0, h / 2 - 5, "horizontal");
                AddConnector("h_right", w - 10, h / 2 - 5, "horizontal");
                // two vertical connectors (top+bottom mid)
                AddConnector("v_top", w / 2 - 5, 0, "vertical");
                AddConnector("v_bottom", w / 2 - 5, h - 10, "vertical");
            }
            else // Ellipse default: allow all around (4 mid points)
            {
                AddConnector("top", w / 2 - 5, 0, "any");
                AddConnector("right", w - 10, h / 2 - 5, "any");
                AddConnector("bottom", w / 2 - 5, h - 10, "any");
                AddConnector("left", 0, h / 2 - 5, "any");
            }

            return result;
        }

        // -------------------- Connector click -> create connection line --------------------
        

        private BlockData? FindBlockById(string id) => currentProject?.Blocks.FirstOrDefault(b => b.Id == id);

        // get absolute canvas center position of the connector ellipse
        private Point GetConnectorCanvasPosition(Ellipse connEllipse, BlockData block)
        {
            if(connEllipse == null || FlowCanvas == null) return new Point(block.X, block.Y);

            try
            {
                // FlowCanvasの中にある場合のみ変換
                if (FlowCanvas.IsAncestorOf(connEllipse))
                {
                    var transform = connEllipse.TransformToAncestor(FlowCanvas);
                    var point = transform.Transform(new Point(connEllipse.Width / 2, connEllipse.Height / 2));
                    return point;
                }
            }
            catch
            {
                // TransformToAncestor中に例外が出た場合もフォールバック
            }

            // フォールバック：位置を推定
            var panel = block.PanelRef;
            if (panel != null)
            {
                double left = Canvas.GetLeft(panel);
                double top = Canvas.GetTop(panel);

                var margin = connEllipse.Margin;
                double x = left + margin.Left + connEllipse.Width / 2;
                double y = top + margin.Top + connEllipse.Height / 2;
                return new Point(x, y);
            }

            return new Point(block.X, block.Y);

        }

        // -------------------- Block dragging & selection --------------------
        private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Grid panel && panel.Tag is BlockData b)
            {
                selectedBlock = b;
                draggingPanel = panel;
                dragStartPos = e.GetPosition(FlowCanvas);
                isDragging = true;

                // set right pane UI
                MemoSummaryBox.Text = b.Summary;
                MemoContentBox.Text = b.Memo;
                StatusBox.Text = b.Status;
                StatusColorCanvas.Background = GetStatusBrush(b.Status);
                AuthorDisplay.Text = $"管理者: {currentProject?.Author ?? "-"}";
            }
            
        }

        private void Block_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Grid panel && panel.Tag is BlockData block)
            {
                ShowBlockDeleteDialog(block, panel);
            }
        }


        protected void ShowBlockDeleteDialog(BlockData block, Grid panel)
        {
            var result = MessageBox.Show(
                "このブロックを削除しますか？",
                "確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                DeleteBlock(block.Id);
            }
        }

        public void DeleteBlock(string blockId)
        {
            // 1. プロジェクトデータからブロックデータを取得
            var block = currentProject?.Blocks.FirstOrDefault(b => b.Id == blockId);
            if (block == null) return;

            // 2. 画面からブロック UI を削除
            if (block.PanelRef != null)
            {
                FlowCanvas.Children.Remove(block.PanelRef);
            }

            // 3. このブロックに接続している線（Connection）を全部検索
            var relatedConnections = currentProject?.Connections
                .Where(c => c.FromBlockId == blockId || c.ToBlockId == blockId)
                .ToList();

            // 4. 線（Line）を UI から削除
            foreach (var conn in relatedConnections)
            {
                if (conn.LineRef != null)
                {
                    FlowCanvas.Children.Remove(conn.LineRef);
                }
            }

            // 5. データ上からも削除
            currentProject?.Connections.RemoveAll(c => c.FromBlockId == blockId || c.ToBlockId == blockId);
            currentProject?.Blocks.Remove(block);
        }



        private void Block_MouseMove(object sender, MouseEventArgs e)
        {
            if (!isDragging || draggingPanel == null || selectedBlock == null) return;
            Point pos = e.GetPosition(FlowCanvas);
            double dx = pos.X - dragStartPos.X;
            double dy = pos.Y - dragStartPos.Y;

            selectedBlock.X += dx;
            selectedBlock.Y += dy;
            Canvas.SetLeft(draggingPanel, selectedBlock.X);
            Canvas.SetTop(draggingPanel, selectedBlock.Y);

            // 接続線の追従更新
            UpdateConnectionsForBlock(selectedBlock);

            dragStartPos = pos;
        }

        public void Block_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            isDragging = false;
            draggingPanel = null;
            SaveProjectsToFile();
            
        }

        private void UpdateConnectionsForBlock(BlockData block)
        {
            if(block == null || currentProject == null) return;
            // iterate lines and recalc positions
            foreach (var conn in currentProject.Connections)
            {
                if(conn.LineRef == null) continue;
                if (conn.FromBlockId == block.Id || conn.ToBlockId == block.Id)
                {
                    var pos = GetConnectorLinePosition(conn);
                    // 線の座標更新
                    conn.LineRef.X1 = pos.X1;
                    conn.LineRef.Y1 = pos.Y1;
                    conn.LineRef.X2 = pos.X2;
                    conn.LineRef.Y2 = pos.Y2;

                    var startBlock = FindBlockById(conn.FromBlockId);
                    var endBlock = FindBlockById(conn.ToBlockId);
                    if (startBlock == null || endBlock == null) continue;

                    var startConn = FindConnectorEllipse(startBlock, conn.FromConnector);
                    var endConn = FindConnectorEllipse(endBlock, conn.ToConnector);
                    if (startConn == null || endConn == null) continue;

                    // 座標取得
                    Point s = GetConnectorCanvasPosition(startConn,startBlock);
                    Point t = GetConnectorCanvasPosition(endConn,endBlock);

                    // UI上の線(LineRef)がなければ新しく作る
                    if (conn.LineRef == null)
                    {
                        var line = new Line
                        {
                            Stroke = Brushes.Black,
                            StrokeThickness = 2,
                            IsHitTestVisible = false
                        };
                        FlowCanvas.Children.Add(line);
                        conn.LineRef = line;
                    }

                   
                }
            }
        }

        private Ellipse? FindConnectorEllipse(BlockData block, string connectorName)
        {
            if (block.PanelRef == null) return null;

            // ブロック内（PanelRefのChildren）を直接探す
            foreach (var el in block.PanelRef.Children.OfType<Ellipse>())
            {
                if (el.Tag is ConnectorTag tag && tag.Name == connectorName)
                    return el;
            }
            return null;
        }

        // -------------------- Apply changes to block (save summary/memo/status) --------------------
        private void ApplyChanges_Click(object sender, RoutedEventArgs e)
        {
            if (selectedBlock == null) { MessageBox.Show("ブロックを選択してください"); return; }
            selectedBlock.Summary = MemoSummaryBox.Text;
            selectedBlock.Memo = MemoContentBox.Text;
            selectedBlock.Status = (StatusBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? selectedBlock.Status;

            // update UI text and color
            if (selectedBlock.TextBlockRef != null) selectedBlock.TextBlockRef.Text = selectedBlock.Summary ?? "";
            // update panel shape color
            if (selectedBlock.PanelRef != null)
            {
                var shape = selectedBlock.PanelRef.Children.OfType<Shape>().FirstOrDefault();
                if (shape != null) shape.Fill = GetStatusBrush(selectedBlock.Status);
            }
            StatusColorCanvas.Background = GetStatusBrush(selectedBlock.Status);
            SaveProjectsToFile();
        }

        // -------------------- Status Box selection changed -> update preview color --------------------
        private void StatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            string s = (StatusBox.SelectedItem as ComboBoxItem)?.Content.ToString() ?? "未着手";
            StatusColorCanvas.Background = GetStatusBrush(s);
        }

        // -------------------- Placeholders for summary and content --------------------
        private void MemoSummaryBox_GotFocus(object sender, RoutedEventArgs e) { if (MemoSummaryBox.Text == "(概要)") MemoSummaryBox.Text = ""; MemoSummaryBox.Foreground = Brushes.Black; }
        private void MemoSummaryBox_LostFocus(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(MemoSummaryBox.Text)) { MemoSummaryBox.Text = "(概要)"; MemoSummaryBox.Foreground = Brushes.Gray; } }
        private void MemoContentBox_GotFocus(object sender, RoutedEventArgs e) { if (MemoContentBox.Text == "(メモ内容)") MemoContentBox.Text = ""; MemoContentBox.Foreground = Brushes.Black; }
        private void MemoContentBox_LostFocus(object sender, RoutedEventArgs e) { if (string.IsNullOrWhiteSpace(MemoContentBox.Text)) { MemoContentBox.Text = "(メモ内容)"; MemoContentBox.Foreground = Brushes.Gray; } }

        // -------------------- Helpers --------------------
        private Brush GetStatusBrush(string status) => status switch
        {
            "進行中" => Brushes.Yellow,
            "完了" => Brushes.LightGreen,
            "中断" => Brushes.LightGray,
            _ => Brushes.White
        };

        private string GetProjectFilesFolder(string projectName)
            => IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, $"files_{SanitizeFileName(projectName)}");

        private static string SanitizeFileName(string s)
        {
            foreach (var c in IOPath.GetInvalidFileNameChars())
                s = s.Replace(c, '_');
            return s;
        }

        private void ExportProjectAsHtml(ProjectData project)
        {
            if (project == null) return;

            var dlg = new SaveFileDialog
            {
                Filter = "HTMLファイル (*.html)|*.html",
                FileName = $"{project.Name}_FlowReport.html"
            };

            if (dlg.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine("<html><head><meta charset='UTF-8'><style>");
                sb.AppendLine("body { font-family: sans-serif; margin: 20px; }");
                sb.AppendLine("h1 { border-bottom: 2px solid #888; }");
                sb.AppendLine("table { border-collapse: collapse; width: 100%; margin-bottom: 20px; }");
                sb.AppendLine("td, th { border: 1px solid #ccc; padding: 6px; }");
                sb.AppendLine("</style></head><body>");
                sb.AppendLine($"<h1>{project.Name}</h1>");

                foreach (var block in project.Blocks)
                {
                    sb.AppendLine("<table>");
                    sb.AppendLine($"<tr><th colspan='2'>{block.Summary}</th></tr>");
                    sb.AppendLine($"<tr><td width='30%'>概要</td><td>{block.Summary}</td></tr>");
                    sb.AppendLine($"<tr><td>メモ</td><td>{block.Memo}</td></tr>");
                    sb.AppendLine($"<tr><td>状態</td><td>{block.Status}</td></tr>");
                    sb.AppendLine("</table>");
                }

                sb.AppendLine("</body></html>");
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show("HTMLファイルを出力しました。");
            }
        }

        private void ExportCanvasAsImage_Click(object sender, RoutedEventArgs e)
        {
            // あなたのCanvasコントロールの名前が FlowCanvas だと仮定
            ExportProjectAsHtml(currentProject);
        }

    }

    // -------------------- Helper Window for deleting files --------------------
    public partial class DeleteFilesWindow : Window
    {
        private ProjectData project;
        public DeleteFilesWindow(ProjectData proj)
        {
            project = proj;
            BuildUi();
        }

        private ListBox? listBox;
        private void BuildUi()
        {
            Title = "添付ファイルの削除";
            Width = 400; Height = 300;
            var stack = new StackPanel();
            listBox = new ListBox() { Height = 200, Margin = new Thickness(8) };
            foreach (var f in project.Files) listBox.Items.Add(f);
            stack.Children.Add(listBox);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(8) };
            var delBtn = new Button { Content = "削除", Width = 80, Margin = new Thickness(4) };
            delBtn.Click += (s, e) => { DeleteSelected(); };
            var cancel = new Button { Content = "キャンセル", Width = 80, Margin = new Thickness(4) };
            cancel.Click += (s, e) => { DialogResult = false; Close(); };
            btnPanel.Children.Add(delBtn); btnPanel.Children.Add(cancel);
            stack.Children.Add(btnPanel);

            Content = stack;
        }

        private void DeleteSelected()
        {
            if (listBox?.SelectedItem == null) { MessageBox.Show("削除するファイルを選択してください"); return; }
            string filename = listBox.SelectedItem.ToString() ?? "";
            string folder = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"files_{SanitizeFileName(project.Name)}");
            string full = System.IO.Path.Combine(folder, filename);
            if (File.Exists(full))
            {
                try { File.Delete(full); }
                catch { MessageBox.Show("ファイル削除に失敗しました"); return; }
            }
            project.Files.Remove(filename);
            listBox.Items.Remove(filename);
            DialogResult = true;
            Close();
        }

        private static string SanitizeFileName(string s)
        {
            foreach (var c in IOPath.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }
    }

    // -------------------- Data Models --------------------
    public class ProjectData
    {
        public string Name { get; set; } = string.Empty;
        public DateTime StartDate { get; set; }
        public string Author { get; set; } = string.Empty;
        public List<BlockData> Blocks { get; set; } = new();
        public List<ConnectionData> Connections { get; set; } = new();
        public List<string> Files { get; set; } = new();
    }

    public class BlockData
    {
        public string Id { get; set; } = string.Empty;
        public string Shape { get; set; } = string.Empty;
        public double X { get; set; } = 0;
        public double Y { get; set; } = 0;
        public string Summary { get; set; } = string.Empty;
        public string Memo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;

        // runtime references (not serialized)
        [JsonIgnore]
        public Grid? PanelRef { get; set; }
        [JsonIgnore]
        public TextBlock? TextBlockRef { get; set; }
        [JsonIgnore]
        public List<ConnectorInfo>? Connectors { get; set; }
    }

    public class ConnectorInfo
    {
        public string Name { get; set; } = string.Empty;
        [JsonIgnore]
        public Ellipse? UI { get; set; }
        public string Allowed { get; set; } = "any";
    }

    public class ConnectorTag
    {
        public string BlockId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Allowed { get; set; } = "any";
    }

    public class ConnectionData
    {
        public string FromBlockId { get; set; } = string.Empty;
        public string FromConnector { get; set; } = string.Empty;
        public string ToBlockId { get; set; } = string.Empty;
        public string ToConnector { get; set; } = string.Empty;

        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        [JsonIgnore] public Line? LineRef { get; set; }
    }
}
