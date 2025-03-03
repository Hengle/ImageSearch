﻿using Masuit.Tools.Logging;
using Masuit.Tools.Media;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Masuit.Tools.Files.FileDetector;
using Masuit.Tools.Systems;
using Image = SixLabors.ImageSharp.Image;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace 以图搜图;

public partial class Form1 : Form
{
    public Form1()
    {
        InitializeComponent();
        CheckForIllegalCrossThreadCalls = false;
    }

    private async void Form1_Load(object sender, EventArgs e)
    {
        var bitmap = new Bitmap(picSource.Width, picSource.Height);
        using var graphics = Graphics.FromImage(bitmap);
        string text = "单击这里选择需要检索的图片";
        Font font = new Font("微软雅黑LightUI", 9);
        Brush brush = new SolidBrush(Color.Black);
        graphics.DrawString(text, font, brush, new PointF(10, 10));
        picSource.Image = bitmap;
        lbIndexCount.Text = "正在加载索引...";
        if (File.Exists("index.json"))
        {
            _index = await JsonSerializer.DeserializeAsync<ConcurrentDictionary<string, ulong[]>>(File.OpenRead("index.json")).ConfigureAwait(false);
        }
        if (File.Exists("frame_index.json"))
        {
            _frameIndex = await JsonSerializer.DeserializeAsync<ConcurrentDictionary<string, List<ulong[]>>>(File.OpenRead("frame_index.json")).ConfigureAwait(false);
        }

        if (_index.Count + _frameIndex.Count > 0)
        {
            lbIndexCount.Text = _index.Count + _frameIndex.Count + "文件";
        }
        else
        {
            lbIndexCount.Text = "请先创建索引";
        }
    }

    private void btnDirectory_Click(object sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog();
        var result = dialog.ShowDialog();
        if (result == DialogResult.OK)
        {
            txtDirectory.Text = dialog.SelectedPath;
        }
    }

    private void btnPic_Click(object sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog();
        if (dialog.ShowDialog() == DialogResult.OK)
        {
            txtPic.Text = dialog.FileName;
            picSource.ImageLocation = txtPic.Text;
            picSource.CreateGraphics().Clear(Color.White);
        }
    }

    private ConcurrentDictionary<string, ulong[]> _index = new();
    private ConcurrentDictionary<string, List<ulong[]>> _frameIndex = new();
    private static readonly ReaderWriterLockSlim ReaderWriterLock = new();
    private bool _removingInvalidIndex;
    private bool IndexRunning { get; set; }

    private readonly Regex picRegex = new Regex("(jpg|jpeg|png|bmp)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private async void btnIndex_Click(object sender, EventArgs e)
    {
        if (IndexRunning)
        {
            IndexRunning = false;
            btnIndex.Text = "更新索引";
            return;
        }

        if (string.IsNullOrEmpty(txtDirectory.Text))
        {
            MessageBox.Show("请先选择文件夹", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            return;
        }

        if (cbRemoveInvalidIndex.Checked)
        {
            _ = Task.Run(() =>
            {
                _removingInvalidIndex = true;
                foreach (var key in _index.Keys.Except(_index.Keys.GroupBy(x => string.Join('\\', x.Split('\\')[..2])).SelectMany(g =>
                         {
                             if (Directory.Exists(g.Key))
                             {
                                 if (File.Exists("Everything64.dll") && Process.GetProcessesByName("Everything").Length > 0)
                                 {
                                     return EverythingHelper.EnumerateFiles(FindLCP(g.ToArray()), "*.jpg|*.jpeg|*.bmp|*.png");
                                 }

                                 return Directory.EnumerateFiles(FindLCP(g.ToArray()), "*", new EnumerationOptions()
                                 {
                                     IgnoreInaccessible = true,
                                     AttributesToSkip = FileAttributes.System | FileAttributes.Temporary,
                                     RecurseSubdirectories = true
                                 }).Where(s => picRegex.IsMatch(s));
                             }

                             return [];
                         })).AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Where(s => !File.Exists(s)))
                {
                    _index.TryRemove(key, out _);
                }

                foreach (var key in _frameIndex.Keys.Except(_frameIndex.Keys.GroupBy(x => string.Join('\\', x.Split('\\')[..2])).SelectMany(g =>
                         {
                             if (Directory.Exists(g.Key))
                             {
                                 if (File.Exists("Everything64.dll") && Process.GetProcessesByName("Everything").Length > 0)
                                 {
                                     return EverythingHelper.EnumerateFiles(FindLCP(g.ToArray()), "*.gif");
                                 }

                                 return Directory.EnumerateFiles(FindLCP(g.ToArray()), "*.gif", new EnumerationOptions()
                                 {
                                     IgnoreInaccessible = true,
                                     AttributesToSkip = FileAttributes.System | FileAttributes.Temporary,
                                     RecurseSubdirectories = true
                                 });
                             }

                             return [];
                         })).AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Where(s => !File.Exists(s)))
                {
                    _frameIndex.TryRemove(key, out _);
                }

                ReaderWriterLock.EnterWriteLock();
                File.WriteAllText("index.json", JsonSerializer.Serialize(_index), Encoding.UTF8);
                File.WriteAllText("frame_index.json", JsonSerializer.Serialize(_frameIndex), Encoding.UTF8);
                ReaderWriterLock.ExitWriteLock();
                _removingInvalidIndex = false;
                lbIndexCount.Text = _index.Count + _frameIndex.Count + "文件";
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    LogManager.Error(t.Exception);
                    _removingInvalidIndex = false;
                }
            }).ConfigureAwait(false);
        }

        IndexRunning = true;
        btnIndex.Text = "停止索引";
        cbRemoveInvalidIndex.Hide();
        var imageHasher = new ImageHasher(new ImageSharpTransformer());
        lblProcess.Text = "正在扫描文件...";
        var files = File.Exists("Everything64.dll") && Process.GetProcessesByName("Everything").Length > 0 ? EverythingHelper.EnumerateFiles(txtDirectory.Text).ToArray() : Directory.GetFiles(txtDirectory.Text, "*", SearchOption.AllDirectories);
        int? filesCount = files.Except(_index.Keys).Count(s => Regex.IsMatch(s, "(gif|jpg|jpeg|png|bmp)$", RegexOptions.IgnoreCase));
        var local = new ThreadLocal<int>(true);
        await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            long size = 0;
            files.Except(_index.Keys).Where(s => picRegex.IsMatch(s)).Chunk(Environment.ProcessorCount * 2).AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).ForAll(g =>
            {
                foreach (var s in g)
                {
                    if (IndexRunning)
                    {
                        if (lblProcess.InvokeRequired)
                        {
                            local.Value++;
                            lblProcess.Invoke(() => lblProcess.Text = $"{local.Values.Sum()}/{filesCount}");
                        }
                        try
                        {
                            _index.GetOrAdd(s, _ => imageHasher.DifferenceHash256(s));
                            size += new FileInfo(s).Length;
                        }
                        catch
                        {
                            LogManager.Info(s + "格式不正确");
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            });
            files.Where(s => s.EndsWith(".gif", StringComparison.CurrentCultureIgnoreCase)).Except(_frameIndex.Keys).Chunk(Environment.ProcessorCount * 2).AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).ForAll(g =>
            {
                foreach (var s in g)
                {
                    if (IndexRunning)
                    {
                        if (lblProcess.InvokeRequired)
                        {
                            local.Value++;
                            lblProcess.Invoke(() => lblProcess.Text = $"{local.Values.Sum()}/{filesCount}");
                        }
                        try
                        {
                            using var gif = Image.Load<Rgba32>(s);
                            for (var i = 0; i < gif.Frames.Count; i++)
                            {
                                using var frame = gif.Frames.ExportFrame(i);
                                var hash = imageHasher.DifferenceHash256(frame);
                                _frameIndex.GetOrAdd(s, _ => []).Add(hash);
                            }

                            size += new FileInfo(s).Length;
                        }
                        catch
                        {
                            LogManager.Info(s + "格式不正确");
                        }
                    }
                    else
                    {
                        break;
                    }
                }
            });
            lbSpeed.Text = $"索引速度: {Math.Round(local.Values.Sum() * 1.0 / sw.Elapsed.TotalSeconds)} items/s({size * 1f / 1048576 / sw.Elapsed.TotalSeconds:N}MB/s)";
            lbIndexCount.Text = _index.Count + _frameIndex.Count + "文件";
            cbRemoveInvalidIndex.Show();
            ReaderWriterLock.EnterWriteLock();
            File.WriteAllText("index.json", JsonSerializer.Serialize(_index), Encoding.UTF8);
            File.WriteAllText("frame_index.json", JsonSerializer.Serialize(_frameIndex), Encoding.UTF8);
            ReaderWriterLock.ExitWriteLock();
            MessageBox.Show($"索引创建完成，耗时：{sw.Elapsed.TotalSeconds}s", "消息", MessageBoxButtons.OK, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
        }).ConfigureAwait(false);
        IndexRunning = false;
        btnIndex.Text = "更新索引";
    }

    private static string FindLCP(string[] strs)
    {
        if (strs == null || strs.Length == 0) return "";

        // 找到最短的字符串，因为LCP不会超过最短字符串的长度
        string shortest = strs[0];
        for (int i = 1; i < strs.Length; i++)
        {
            if (strs[i].Length < shortest.Length)
                shortest = strs[i];
        }

        // 逐字符比较，直到找到不匹配的字符
        for (int i = 0; i < shortest.Length; i++)
        {
            char c = shortest[i];
            for (int j = 1; j < strs.Length; j++)
            {
                // 如果当前索引越界或字符不匹配，则返回当前LCP
                if (i >= strs[j].Length || strs[j][i] != c)
                    return shortest.Substring(0, i);
            }
        }

        // 如果所有字符串共享整个最短字符串，则返回它
        return shortest;
    }

    private void btnSearch_Click(object sender, EventArgs e)
    {
        if (string.IsNullOrEmpty(txtPic.Text))
        {
            MessageBox.Show("请先选择图片", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            return;
        }

        if (_index.Count == 0)
        {
            MessageBox.Show("当前没有任何索引，请先添加文件夹创建索引后再搜索", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            return;
        }

        if (new FileInfo(txtPic.Text).DetectFiletype().MimeType?.StartsWith("image") != true)
        {
            MessageBox.Show("不是图像文件，无法检索", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);
            return;
        }

        var sim = (float)numLike.Value / 100;
        var hasher = new ImageHasher();
        var sw = Stopwatch.StartNew();
        var hashs = new ConcurrentBag<ulong[]>();
        var actions = new List<Action>();

        if (txtPic.Text.EndsWith("gif"))
        {
            using (var gif = Image.Load<Rgba32>(txtPic.Text))
            {
                for (var i = 0; i < gif.Frames.Count; i++)
                {
                    var frame = gif.Frames.ExportFrame(i);
                    actions.Add(() =>
                    {
                        hashs.Add(frame.DifferenceHash256());
                        frame.Dispose();
                    });
                }
                Parallel.Invoke(actions.ToArray());
            }
        }
        else
        {
            hashs.Add(hasher.DifferenceHash256(txtPic.Text));
            using (var image = Image.Load<Rgba32>(txtPic.Text))
            {
                if (cbRotate.Checked)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(90));
                        var rotate90 = clone.DifferenceHash256();
                        hashs.Add(rotate90);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(180));
                        var rotate180 = clone.DifferenceHash256();
                        hashs.Add(rotate180);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(270));
                        var rotate270 = clone.DifferenceHash256();
                        hashs.Add(rotate270);
                    });
                }

                if (cbFlip.Checked)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Flip(FlipMode.Horizontal));
                        var flipH = clone.DifferenceHash256();
                        hashs.Add(flipH);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Flip(FlipMode.Vertical));
                        var flipV = clone.DifferenceHash256();
                        hashs.Add(flipV);
                    });
                }
                Parallel.Invoke(actions.ToArray());
            }
        }

        var list = new List<SearchResult>();
        if (txtPic.Text.EndsWith("gif"))
        {
            list.AddRange(_frameIndex.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Select(x => new SearchResult
            {
                路径 = x.Key,
                匹配度 = x.Value.SelectMany(h => hashs.Select(hh => ImageHasher.Compare(h, hh))).Where(f => f >= sim).OrderDescending().Take(10).DefaultIfEmpty().Average()
            }).Where(x => x.匹配度 >= sim));
        }
        else
        {
            list.AddRange(_frameIndex.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Select(x => new SearchResult
            {
                路径 = x.Key,
                匹配度 = x.Value.SelectMany(h => hashs.Select(hh => ImageHasher.Compare(h, hh))).Max()
            }).Where(x => x.匹配度 >= sim));
            list.AddRange(_index.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Select(x => new SearchResult
            {
                路径 = x.Key,
                匹配度 = hashs.Select(h => ImageHasher.Compare(x.Value, h)).Max()
            }).Where(x => x.匹配度 >= sim));
        }

        list = list.OrderByDescending(a => a.匹配度).ToList();
        lbElpased.Text = sw.ElapsedMilliseconds + "ms";
        if (list.Count > 0)
        {
            picSource.Refresh();
        }

        dgvResult.DataSource = new BindingList<SearchResult>(list);
        dgvResult.Focus();
    }

    private void dgvResult_CellContentClick(object sender, DataGridViewCellEventArgs e)
    {
    }

    private void dgvResult_CellClick(object sender, DataGridViewCellEventArgs e)
    {
        var location = dgvResult.SelectedCells[0].OwningRow.Cells["路径"].Value.ToString();
        if (File.Exists(location))
        {
            picDest.ImageLocation = location;
        }
        else
        {
            var bitmap = new Bitmap(picSource.Width, picSource.Height);
            using var graphics = Graphics.FromImage(bitmap);
            string text = "文件不存在";
            Font font = new Font("微软雅黑LightUI", 9);
            Brush brush = new SolidBrush(Color.Black);
            graphics.DrawString(text, font, brush, new PointF(10, 10));
            picDest.Image = bitmap;
        }
    }

    private void picSource_LoadCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
    {
        lbSrcInfo.Text = $"分辨率：{picSource.Image.Width}x{picSource.Image.Height}，大小：{new FileInfo(picSource.ImageLocation).Length / 1024}KB";
    }

    private void picDest_LoadCompleted(object sender, System.ComponentModel.AsyncCompletedEventArgs e)
    {
        if (File.Exists(picDest.ImageLocation))
        {
            lblDestInfo.Text = $"分辨率：{picDest.Image.Width}x{picDest.Image.Height}，大小：{new FileInfo(picDest.ImageLocation).Length / 1024}KB";
        }
    }

    private void lblGithub_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = lblGithub.Text, UseShellExecute = true });
    }

    private void txtDirectory_DragEnter(object sender, DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
    }

    private void txtDirectory_DragDrop(object sender, DragEventArgs e)
    {
        ((TextBox)sender).Text = ((System.Array)e.Data.GetData(DataFormats.FileDrop)).GetValue(0).ToString();
    }

    private void txtPic_DragEnter(object sender, DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
    }

    private void txtPic_DragDrop(object sender, DragEventArgs e)
    {
        ((TextBox)sender).Text = ((System.Array)e.Data.GetData(DataFormats.FileDrop)).GetValue(0).ToString();
    }

    private void buttonClipSearch_Click(object sender, EventArgs e)
    {
        if (Clipboard.ContainsFileDropList())
        {
            txtPic.Text = Clipboard.GetFileDropList()[0];
            picSource.ImageLocation = txtPic.Text;
            btnSearch_Click(sender, e);
            return;
        }

        if (Clipboard.ContainsText())
        {
            var text = Clipboard.GetText();
            if (File.Exists(text))
            {
                picSource.ImageLocation = text;
                btnSearch_Click(sender, e);
            }
            else
            {
                dgvResult.DataSource = null;
                picSource.Image = null;
                picSource.Refresh();
            }
            return;
        }

        if (Clipboard.ContainsImage())
        {
            using var sourceImage = Clipboard.GetImage();
            var filename = Path.Combine(Environment.GetEnvironmentVariable("temp"), SnowFlake.NewId);
            sourceImage.Save(filename, ImageFormat.Jpeg);
            picSource.ImageLocation = filename;
            var sim = (float)numLike.Value / 100;
            var hasher = new ImageHasher();
            var sw = Stopwatch.StartNew();
            var hash = hasher.DifferenceHash256(filename);
            var hashs = new ConcurrentBag<ulong[]> { hash };
            using (var image = Image.Load<Rgba32>(filename))
            {
                var actions = new List<Action>();
                if (cbRotate.Checked)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(90));
                        var rotate90 = clone.DifferenceHash256();
                        hashs.Add(rotate90);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(180));
                        var rotate180 = clone.DifferenceHash256();
                        hashs.Add(rotate180);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Rotate(270));
                        var rotate270 = clone.DifferenceHash256();
                        hashs.Add(rotate270);
                    });
                }

                if (cbFlip.Checked)
                {
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Flip(FlipMode.Horizontal));
                        var flipH = clone.DifferenceHash256();
                        hashs.Add(flipH);
                    });
                    actions.Add(() =>
                    {
                        using var clone = image.Clone(c => c.Flip(FlipMode.Vertical));
                        var flipV = clone.DifferenceHash256();
                        hashs.Add(flipV);
                    });
                }
                Parallel.Invoke(actions.ToArray());
            }

            var list = _frameIndex.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Select(x => new SearchResult()
            {
                路径 = x.Key,
                匹配度 = x.Value.SelectMany(h => hashs.Select(hh => ImageHasher.Compare(h, hh))).Max()
            }).Where(x => x.匹配度 >= sim).ToList();
            list.AddRange(_index.AsParallel().WithDegreeOfParallelism(Environment.ProcessorCount * 2).Select(x => new SearchResult()
            {
                路径 = x.Key,
                匹配度 = hashs.Select(h => ImageHasher.Compare(x.Value, h)).Max()
            }).Where(x => x.匹配度 >= sim));
            list = list.OrderByDescending(a => a.匹配度).ToList();
            lbElpased.Text = sw.ElapsedMilliseconds + "ms";
            if (list.Count > 0)
            {
                picSource.ImageLocation = filename;
                picSource.Refresh();
            }

            dgvResult.DataSource = new BindingList<SearchResult>(list);
            Task.Run(() =>
            {
                Thread.Sleep(1000);
                File.Delete(filename);
            }).ContinueWith(_ => 0).ConfigureAwait(false);
        }
    }

    private void dgvResult_CellMouseDown(object sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right && dgvResult.SelectedCells.Count > 0)
        {
            dgvContextMenuStrip.Show(MousePosition.X, MousePosition.Y);
            return;
        }

        dgvContextMenuStrip.Hide();
    }

    private void dgvResult_CellDoubleClick(object sender, DataGridViewCellEventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = dgvResult.SelectedCells[0].OwningRow.Cells["路径"].Value.ToString(), UseShellExecute = true });
    }

    /// <summary>
    /// 打开路径并定位文件...对于@"h:\Bleacher Report - Hardaway with the safe call ??.mp4"这样的，explorer.exe /select,d:xxx不认，用API整它
    /// </summary>
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void ILFree(IntPtr pidlList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(IntPtr pidlList, uint cild, IntPtr children, uint dwFlags);

    public void ExplorerFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var pidlList = ILCreateFromPathW(filePath);
        if (pidlList != IntPtr.Zero)
        {
            try
            {
                Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(pidlList, 0, IntPtr.Zero, 0));
            }
            catch
            {
            }
            finally
            {
                ILFree(pidlList);
            }
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.V))
        {
            buttonClipSearch_Click(null, null);
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void dgvResult_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Delete)
        {
            foreach (DataGridViewCell cell in dgvResult.SelectedCells)
            {
                var path = cell.OwningRow.Cells[0].Value?.ToString();
                if (path is not null)
                {
                    if (e.Modifiers is Keys.Shift)
                    {
                        RecycleBinHelper.Delete(path);
                    }
                    else
                    {
                        File.Delete(path);
                    }
                    dgvResult.Rows.RemoveAt(cell.RowIndex);
                    _index.TryRemove(path, out _);
                    _frameIndex.TryRemove(path, out _);
                }
            }

            Task.Run(() =>
            {
                _removingInvalidIndex = true;
                ReaderWriterLock.EnterWriteLock();
                File.WriteAllText("index.json", JsonSerializer.Serialize(_index), Encoding.UTF8);
                File.WriteAllText("frame_index.json", JsonSerializer.Serialize(_frameIndex), Encoding.UTF8);
                ReaderWriterLock.ExitWriteLock();
                _removingInvalidIndex = false;
            }).ConfigureAwait(false);
        }

        if (e.Modifiers == Keys.Control && e.KeyCode is Keys.O)
        {
            ExplorerFile(dgvResult.SelectedCells[0].OwningRow.Cells["路径"].Value.ToString());
        }
    }

    private void dgvResult_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Up or Keys.Down)
        {
            var location = dgvResult.SelectedCells[0].OwningRow.Cells["路径"].Value.ToString();
            if (File.Exists(location))
            {
                picDest.ImageLocation = location;
            }
            else
            {
                var bitmap = new Bitmap(picSource.Width, picSource.Height);
                using var graphics = Graphics.FromImage(bitmap);
                string text = "文件不存在";
                Font font = new Font("微软雅黑LightUI", 9);
                Brush brush = new SolidBrush(Color.Black);
                graphics.DrawString(text, font, brush, new PointF(10, 10));
                picDest.Image = bitmap;
            }
        }
    }

    private void picSource_DoubleClick(object sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = picSource.ImageLocation, UseShellExecute = true });
    }

    private void picDest_Click(object sender, EventArgs e)
    {
        Process.Start(new ProcessStartInfo { FileName = picDest.ImageLocation, UseShellExecute = true });
    }

    private void Form1_FormClosing(object sender, FormClosingEventArgs e)
    {
        if (IndexRunning)
        {
            MessageBox.Show("正在索引文件，关闭程序前请先取消", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            e.Cancel = true;
            return;
        }

        if (_removingInvalidIndex)
        {
            MessageBox.Show("正在移除无效的索引文件，请稍后再试", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button1);
            e.Cancel = true;
            return;
        }

        ReaderWriterLock.Dispose();
    }

    private void picSource_Click(object sender, EventArgs e)
    {
        btnPic_Click(sender, e);
    }

    private void 打开所在文件夹_Click(object sender, EventArgs e)
    {
        ExplorerFile(dgvResult.SelectedCells[0].OwningRow.Cells["路径"].Value.ToString());
    }

    private void 删除_Click(object sender, EventArgs e)
    {
        var result = MessageBox.Show("确认删除选中项吗？", "提示", MessageBoxButtons.OKCancel);
        if (result == DialogResult.OK)
        {
            foreach (DataGridViewCell cell in dgvResult.SelectedCells)
            {
                var path = cell.OwningRow.Cells[0].Value?.ToString();
                if (path is not null)
                {
                    File.Delete(path);
                    dgvResult.Rows.RemoveAt(cell.RowIndex);
                    _index.TryRemove(path, out _);
                    _frameIndex.TryRemove(path, out _);
                }
            }

            Task.Run(() =>
            {
                _removingInvalidIndex = true;
                ReaderWriterLock.EnterWriteLock();
                File.WriteAllText("index.json", JsonSerializer.Serialize(_index), Encoding.UTF8);
                File.WriteAllText("frame_index.json", JsonSerializer.Serialize(_frameIndex), Encoding.UTF8);
                ReaderWriterLock.ExitWriteLock();
                _removingInvalidIndex = false;
            }).ConfigureAwait(false);
        }
    }

    private void 删除到回收站ToolStripMenuItem_Click(object sender, EventArgs e)
    {
        var result = MessageBox.Show("确认删除选中项吗？", "提示", MessageBoxButtons.OKCancel);
        if (result == DialogResult.OK)
        {
            foreach (DataGridViewCell cell in dgvResult.SelectedCells)
            {
                var path = cell.OwningRow.Cells[0].Value?.ToString();
                if (path is not null)
                {
                    RecycleBinHelper.Delete(path);
                    dgvResult.Rows.RemoveAt(cell.RowIndex);
                    _index.TryRemove(path, out _);
                    _frameIndex.TryRemove(path, out _);
                }
            }

            Task.Run(() =>
            {
                _removingInvalidIndex = true;
                ReaderWriterLock.EnterWriteLock();
                File.WriteAllText("index.json", JsonSerializer.Serialize(_index), Encoding.UTF8);
                File.WriteAllText("frame_index.json", JsonSerializer.Serialize(_frameIndex), Encoding.UTF8);
                ReaderWriterLock.ExitWriteLock();
                _removingInvalidIndex = false;
            }).ConfigureAwait(false);
        }
    }
}

public record SearchResult
{
    public string 路径 { get; set; }
    public float 匹配度 { get; set; }
}