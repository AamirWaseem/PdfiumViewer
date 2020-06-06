﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Image = System.Windows.Controls.Image;
using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

namespace PdfiumViewer
{
    public class PdfRenderer : ScrollViewer, IPdfDocument, INotifyPropertyChanged
    {
        public PdfRenderer()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            Effect = new DropShadowEffect()
            {
                BlurRadius = 10,
                Direction = 270,
                RenderingBias = RenderingBias.Performance,
                ShadowDepth = 0
            };
            Panel = new StackPanel()
            {
                HorizontalAlignment = HorizontalAlignment.Center
            };
            VirtualizingPanel.SetIsVirtualizing(Panel, true);
            VirtualizingPanel.SetVirtualizationMode(Panel, VirtualizationMode.Recycling);
            Content = Panel;

            ZoomMode = PdfViewerZoomMode.FitHeight;
            PagesDisplayMode = PdfViewerPagesDisplayMode.SinglePageMode;
            Dpi = 96;
            ScrollWidth = 50;
            FrameSpace = new Thickness(5);
            RenderedFramesMap = new ConcurrentDictionary<int, Image>();
        }

        protected Process CurrentProcess { get; } = Process.GetCurrentProcess();
        protected PdfDocument Document { get; set; }
        protected StackPanel Panel { get; set; }
        protected Thickness FrameSpace { get; set; }
        protected Image Frame1 => Frames?.FirstOrDefault();
        protected Image Frame2 => Frames?.Length > 1 ? Frames[1] : null;
        protected Image[] Frames { get; set; }
        protected ConcurrentDictionary<int, Image> RenderedFramesMap { get; set; }
        protected Size CurrentPageSize { get; set; }
        protected int ScrollWidth { get; set; }
        private bool DoZoom { get; set; }

        public event PropertyChangedEventHandler PropertyChanged;
        public const double DefaultZoomMin = 0.1;
        public const double DefaultZoomMax = 5;
        public const double DefaultZoomFactor = 1.2;
        public int PageNo { get; set; }
        public int Dpi { get; set; }
        /// <summary>
        /// Gets or sets the current zoom level.
        /// </summary>
        [Browsable(false)]
        [DefaultValue(1.0)]
        public double Zoom { get; set; }
        [DefaultValue(DefaultZoomMin)] public double ZoomMin { get; set; }
        [DefaultValue(DefaultZoomMax)] public double ZoomMax { get; set; }
        [DefaultValue(DefaultZoomFactor)] public double ZoomFactor { get; set; }
        public PdfViewerZoomMode ZoomMode { get; set; }
        public PdfViewerPagesDisplayMode PagesDisplayMode { get; set; }
        public bool IsDocumentLoaded => Document != null;


        protected void OnPageNoChanged()
        {
            GotoPage(PageNo);
        }
        protected void OnDpiChanged()
        {
            GotoPage(PageNo);
        }
        protected void OnPagesDisplayModeChanged()
        {
            Panel.Children.Clear();
            RenderedFramesMap?.Clear();

            if (PagesDisplayMode == PdfViewerPagesDisplayMode.SinglePageMode)
            {
                Frames = new Image[1];
                Panel.Orientation = Orientation.Horizontal;
            }
            else if (PagesDisplayMode == PdfViewerPagesDisplayMode.BookMode)
            {
                Frames = new Image[2];
                Panel.Orientation = Orientation.Horizontal;
            }
            else if (PagesDisplayMode == PdfViewerPagesDisplayMode.ContinuousMode && IsDocumentLoaded)
            {
                // frames created at scrolling
                Frames = new Image[Document.PageCount];
                Panel.Orientation = Orientation.Vertical;
            }

            for (var i = 0; i < Frames.Length; i++)
            {
                if (Frames[i] == null)
                    Frames[i] = new Image() { Margin = FrameSpace };

                if (IsDocumentLoaded)
                {
                    var pageSize = CalculatePageSize(i);
                    Frames[i].Width = pageSize.Width;
                    Frames[i].Height = pageSize.Height;
                }

                Panel.Children.Add(Frames[i]);
            }

            if (PagesDisplayMode == PdfViewerPagesDisplayMode.ContinuousMode && IsDocumentLoaded)
                Frames[PageNo].BringIntoView(); // scroll to current page

            GC.Collect();
            GotoPage(PageNo);
        }
        protected void OnZoomModeChanged()
        {
            OnPagesDisplayModeChanged();
        }
        protected Size CalculatePageSize(int? page = null)
        {
            page ??= PageNo;
            var containerWidth = ActualWidth - Padding.Left - Padding.Right; // ViewportWidth
            var containerHeight = ActualHeight - Padding.Top - Padding.Bottom; // ViewportHeight

            if (IsDocumentLoaded)
            {
                var currentPageSize = Document.PageSizes[page.Value];
                var whRatio = currentPageSize.Width / currentPageSize.Height;

                var height = containerHeight;
                var width = whRatio * height;

                if (ZoomMode == PdfViewerZoomMode.FitWidth)
                {
                    width = containerWidth - ScrollWidth;
                    if (PagesDisplayMode == PdfViewerPagesDisplayMode.BookMode)
                        width /= 2;
                    height = (int)(1 / whRatio * width);
                }

                return new Size((int)width, (int)height);
            }

            return new Size();
        }
        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            GotoPage(PageNo);
        }
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            if (DoZoom)
            {
                e.Handled = true;
                if (e.Delta > 0)
                    ZoomIn();
                else
                    ZoomOut();
            }
            else if(PagesDisplayMode == PdfViewerPagesDisplayMode.SinglePageMode &&
                    ViewportHeight > Frame1.Height)
            {
                
            }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
                DoZoom = true;
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);

            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
                DoZoom = false;
        }
        protected override void OnScrollChanged(ScrollChangedEventArgs e)
        {
            base.OnScrollChanged(e);

            if (PagesDisplayMode == PdfViewerPagesDisplayMode.ContinuousMode)
            {
                var startOffset = e.VerticalOffset;
                var height = e.ViewportHeight;
                var pageSize = CalculatePageSize(0);

                var startFrameIndex = startOffset / (pageSize.Height + FrameSpace.Top + FrameSpace.Bottom);
                var endFrameIndex = (startOffset + height) / (pageSize.Height + FrameSpace.Top + FrameSpace.Bottom);

                PageNo = (int)Math.Min(Math.Max(startFrameIndex, 0), PageCount - 1);
                var endPageIndex = (int)Math.Min(Math.Max(endFrameIndex, 0), PageCount - 1);

                ReleaseFrames(PageNo, endPageIndex);

                for (var page = PageNo; page <= endPageIndex; page++)
                {
                    var frame = Frames[page];
                    if (IsUserVisible(frame) && frame.Source == null)
                    {
                        RenderPage(frame, page, frame.Width, frame.Height);
                        RenderedFramesMap.TryAdd(page, frame);
                    }
                }
            }
        }


        protected void ReleaseFrames(int keepFrom, int keepTo)
        {
            foreach (var page in RenderedFramesMap.Keys)
            {
                if ((page < keepFrom || page > keepTo) &&
                    RenderedFramesMap.TryRemove(page, out var frame))
                {
                    GC.SuppressFinalize(frame.Source);
                    frame.Source = null;
                }
            }

            GC.Collect();
        }

        protected BitmapImage RenderPage(Image frame, int page, double width, double height)
        {
            var image = Document.Render(page, (int)width, (int)height, Dpi, Dpi, false);
            BitmapImage bitmapImage;
            using (var memory = new MemoryStream())
            {
                image.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad; // not a mistake - see below
                bitmapImage.EndInit();
            }
            // Why BitmapCacheOption.OnLoad?
            // It seems counter intuitive, but this flag has two effects:
            // It enables caching if caching is possible, and it causes the load to happen at EndInit().
            // In our case caching is impossible, so all it does it cause the load to happen immediately.

            CurrentProcess?.Refresh();
            GC.Collect();
            Dispatcher.Invoke(() => frame.Source = bitmapImage);
            return bitmapImage;
        }
        public static bool IsUserVisible(UIElement element)
        {
            if (!element.IsVisible)
                return false;
            var container = VisualTreeHelper.GetParent(element) as FrameworkElement;
            if (container == null) throw new ArgumentNullException(nameof(container));

            Rect bounds = element.TransformToAncestor(container).TransformBounds(new Rect(0.0, 0.0, element.RenderSize.Width, element.RenderSize.Height));
            Rect rect = new Rect(0.0, 0.0, container.ActualWidth, container.ActualHeight);
            return rect.IntersectsWith(bounds);
        }
        public void GotoPage(int page)
        {
            if (IsDocumentLoaded)
            {
                CurrentPageSize = CalculatePageSize(page);

                RenderPage(Frame1, page, CurrentPageSize.Width, CurrentPageSize.Height);

                if (PagesDisplayMode == PdfViewerPagesDisplayMode.BookMode && page + 1 < Document.PageCount)
                {
                    RenderPage(Frame2, page + 1, CurrentPageSize.Width, CurrentPageSize.Height);
                }
            }
        }
        public void OpenPdf(string path)
        {
            Document = PdfDocument.Load(path);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(string path, string password)
        {
            Document = PdfDocument.Load(path, password);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(IWin32Window owner, string path)
        {
            Document = PdfDocument.Load(owner, path);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(IWin32Window owner, Stream stream)
        {
            Document = PdfDocument.Load(owner, stream);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(IWin32Window owner, Stream stream, string password)
        {
            Document = PdfDocument.Load(owner, stream, password);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(Stream stream)
        {
            Document = PdfDocument.Load(stream);
            GotoPage(PageNo = 0);
        }
        public void OpenPdf(Stream stream, string password)
        {
            Document = PdfDocument.Load(stream, password);
            GotoPage(PageNo = 0);
        }
        public void NextPage()
        {
            if (IsDocumentLoaded)
            {
                var extentVal = PagesDisplayMode == PdfViewerPagesDisplayMode.BookMode ? 2 : 1;
                PageNo = Math.Min(Math.Max(PageNo + extentVal, 0), PageCount - extentVal);

                if (PagesDisplayMode == PdfViewerPagesDisplayMode.ContinuousMode)
                    Frames[PageNo].BringIntoView(); // scroll to current page
            }
        }
        public void PreviousPage()
        {
            if (IsDocumentLoaded)
            {
                var extentVal = PagesDisplayMode == PdfViewerPagesDisplayMode.BookMode ? 2 : 1;
                PageNo = Math.Min(Math.Max(PageNo - extentVal, 0), PageCount - extentVal);

                if (PagesDisplayMode == PdfViewerPagesDisplayMode.ContinuousMode)
                    Frames[PageNo].BringIntoView(); // scroll to current page
            }
        }
        /// <summary>
        /// Zooms the PDF document in one step.
        /// </summary>
        public void ZoomIn()
        {
            Zoom = Math.Min(Math.Max(Zoom * ZoomFactor, ZoomMin), ZoomMax);
        }

        /// <summary>
        /// Zooms the PDF document out one step.
        /// </summary>
        public void ZoomOut()
        {
            Zoom = Math.Min(Math.Max(Zoom / ZoomFactor, ZoomMin), ZoomMax);
        }


        #region IPdfDocument implementation

        public int PageCount => Document?.PageCount ?? 0;
        public PdfBookmarkCollection Bookmarks => Document?.Bookmarks;
        public IList<SizeF> PageSizes => Document?.PageSizes;

        public void Render(int page, Graphics graphics, float dpiX, float dpiY, Rectangle bounds, bool forPrinting)
        {
            Document.Render(page, graphics, dpiX, dpiY, bounds, forPrinting);
        }

        public void Render(int page, Graphics graphics, float dpiX, float dpiY, Rectangle bounds, PdfRenderFlags flags)
        {
            Document.Render(page, graphics, dpiX, dpiY, bounds, flags);
        }

        public System.Drawing.Image Render(int page, float dpiX, float dpiY, bool forPrinting)
        {
            return Document.Render(page, dpiX, dpiY, forPrinting);
        }

        public System.Drawing.Image Render(int page, float dpiX, float dpiY, PdfRenderFlags flags)
        {
            return Document.Render(page, dpiX, dpiY, flags);
        }

        public System.Drawing.Image Render(int page, int width, int height, float dpiX, float dpiY, bool forPrinting)
        {
            return Document.Render(page, width, height, dpiX, dpiY, forPrinting);
        }

        public System.Drawing.Image Render(int page, int width, int height, float dpiX, float dpiY, PdfRenderFlags flags)
        {
            return Document.Render(page, width, height, dpiX, dpiY, flags);
        }

        public System.Drawing.Image Render(int page, int width, int height, float dpiX, float dpiY, PdfRotation rotate, PdfRenderFlags flags)
        {
            return Document.Render(page, width, height, dpiX, dpiY, rotate, flags);
        }

        public void Save(string path)
        {
            Document.Save(path);
        }

        public void Save(Stream stream)
        {
            Document.Save(stream);
        }

        public PdfMatches Search(string text, bool matchCase, bool wholeWord)
        {
            return Document.Search(text, matchCase, wholeWord);
        }

        public PdfMatches Search(string text, bool matchCase, bool wholeWord, int page)
        {
            return Document.Search(text, matchCase, wholeWord, page);
        }

        public PdfMatches Search(string text, bool matchCase, bool wholeWord, int startPage, int endPage)
        {
            return Document.Search(text, matchCase, wholeWord, startPage, endPage);
        }

        public PrintDocument CreatePrintDocument()
        {
            return Document.CreatePrintDocument();
        }

        public PrintDocument CreatePrintDocument(PdfPrintMode printMode)
        {
            return Document.CreatePrintDocument(printMode);
        }

        public PrintDocument CreatePrintDocument(PdfPrintSettings settings)
        {
            return Document.CreatePrintDocument(settings);
        }

        public PdfPageLinks GetPageLinks(int page, Size size)
        {
            return Document.GetPageLinks(page, size);
        }

        public void DeletePage(int page)
        {
            Document.DeletePage(page);
        }

        public void RotatePage(int page, PdfRotation rotation)
        {
            Document.RotatePage(page, rotation);
        }

        public PdfInformation GetInformation()
        {
            return Document.GetInformation();
        }

        public string GetPdfText(int page)
        {
            return Document.GetPdfText(page);
        }

        public string GetPdfText(PdfTextSpan textSpan)
        {
            return Document.GetPdfText(textSpan);
        }

        public IList<PdfRectangle> GetTextBounds(PdfTextSpan textSpan)
        {
            return Document.GetTextBounds(textSpan);
        }

        public PointF PointToPdf(int page, Point point)
        {
            return Document.PointToPdf(page, point);
        }

        public Point PointFromPdf(int page, PointF point)
        {
            return Document.PointFromPdf(page, point);
        }

        public RectangleF RectangleToPdf(int page, Rectangle rect)
        {
            return Document.RectangleToPdf(page, rect);
        }

        public Rectangle RectangleFromPdf(int page, RectangleF rect)
        {
            return Document.RectangleFromPdf(page, rect);
        }

        #endregion

        public void Dispose()
        {
            Document?.Dispose();
        }
    }
}
