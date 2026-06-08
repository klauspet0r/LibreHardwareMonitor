// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at http://mozilla.org/MPL/2.0/.
// Copyright (C) LibreHardwareMonitor and Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Windows.Forms.Utilities;

namespace LibreHardwareMonitor.Windows.Forms.UI;

/// <summary>
/// A small, borderless, transparent overlay that shows the average CPU and GPU
/// temperature together with a simple sparkline of the last minute for each.
/// </summary>
public class TempOverlayGadget : Gadget
{
    private const int HistorySeconds = 60;
    private const float MinSpan = 5f;

    private static readonly Color CpuColor = Color.FromArgb(255, 240, 150, 70);   // orange
    private static readonly Color GpuColor = Color.FromArgb(255, 95, 205, 115);   // green
    private static readonly Color NetColor = Color.FromArgb(255, 90, 170, 240);   // blue
    private static readonly Color BackgroundColor = Color.FromArgb(180, 22, 22, 22);
    private static readonly Color LabelColor = Color.FromArgb(210, 210, 210, 210);

    private readonly IComputer _computer;
    private readonly Queue<Sample> _cpuHistory = new();
    private readonly Queue<Sample> _gpuHistory = new();

    private readonly Font _valueFont;
    private readonly Font _labelFont;

    public event EventHandler HideRequested;

    public TempOverlayGadget(IComputer computer, PersistentSettings settings)
    {
        _computer = computer;

        _valueFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 13f, FontStyle.Bold);
        _labelFont = new Font(SystemFonts.MessageBoxFont.FontFamily, 7.5f, FontStyle.Bold);

        Size = new Size(210, 129);

        // default position: top-right corner of the primary screen
        Rectangle workingArea = Screen.PrimaryScreen.WorkingArea;
        int defaultX = workingArea.Right - Size.Width - 16;
        int defaultY = workingArea.Top + 16;
        Location = new Point(settings.GetValue("tempOverlay.Location.X", defaultX),
                             settings.GetValue("tempOverlay.Location.Y", defaultY));
        LocationChanged += delegate
        {
            settings.SetValue("tempOverlay.Location.X", Location.X);
            settings.SetValue("tempOverlay.Location.Y", Location.Y);
        };

        AlwaysOnTop = true;

        ContextMenuStrip menu = new();
        ToolStripMenuItem hideItem = new("Hide Overlay");
        hideItem.Click += delegate { HideRequested?.Invoke(this, EventArgs.Empty); };
        menu.Items.Add(hideItem);
        ContextMenuStrip = menu;
    }

    public override void Dispose()
    {
        _valueFont.Dispose();
        _labelFont.Dispose();
        base.Dispose();
    }

    private float? GetCpuTemperature()
    {
        List<float> coreTemps = new();
        List<float> allTemps = new();
        float? package = null;

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Cpu)
                continue;

            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                    continue;

                string name = sensor.Name;
                if (name.StartsWith("Core #") || name.StartsWith("CPU Core #"))
                    coreTemps.Add(sensor.Value.Value);
                else if (package == null && (name.Contains("Package") || name.Contains("Tctl") || name.Contains("Tdie")))
                    package = sensor.Value.Value;

                allTemps.Add(sensor.Value.Value);
            }
        }

        if (coreTemps.Count > 0)
            return coreTemps.Average();
        if (package.HasValue)
            return package;
        return allTemps.Count > 0 ? allTemps.Average() : null;
    }

    private float? GetGpuTemperature()
    {
        List<float> coreTemps = new();
        List<float> allTemps = new();

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.GpuNvidia &&
                hardware.HardwareType != HardwareType.GpuAmd &&
                hardware.HardwareType != HardwareType.GpuIntel)
            {
                continue;
            }

            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Temperature || !sensor.Value.HasValue)
                    continue;

                if (sensor.Name.Contains("Core") || sensor.Name == "GPU")
                    coreTemps.Add(sensor.Value.Value);

                allTemps.Add(sensor.Value.Value);
            }
        }

        if (coreTemps.Count > 0)
            return coreTemps.Average();
        return allTemps.Count > 0 ? allTemps.Average() : null;
    }

    private (float? Download, float? Upload) GetNetworkThroughput()
    {
        float download = 0f;
        float upload = 0f;
        bool hasDownload = false;
        bool hasUpload = false;

        foreach (IHardware hardware in _computer.Hardware)
        {
            if (hardware.HardwareType != HardwareType.Network)
                continue;

            foreach (ISensor sensor in hardware.Sensors)
            {
                if (sensor.SensorType != SensorType.Throughput || !sensor.Value.HasValue)
                    continue;

                if (sensor.Name == "Download Speed")
                {
                    download += sensor.Value.Value;
                    hasDownload = true;
                }
                else if (sensor.Name == "Upload Speed")
                {
                    upload += sensor.Value.Value;
                    hasUpload = true;
                }
            }
        }

        return (hasDownload ? download : null, hasUpload ? upload : null);
    }

    private static string FormatBytesPerSecond(float? bytesPerSecond)
    {
        if (!bytesPerSecond.HasValue)
            return "--";

        string[] units = { "B/s", "KB/s", "MB/s", "GB/s" };
        float value = bytesPerSecond.Value;
        int unit = 0;
        while (value >= 1024f && unit < units.Length - 1)
        {
            value /= 1024f;
            unit++;
        }

        return $"{value:0.0} {units[unit]}";
    }

    private static void UpdateHistory(Queue<Sample> history, float? value, DateTime now)
    {
        if (value.HasValue)
            history.Enqueue(new Sample(now, value.Value));

        while (history.Count > 0 && (now - history.Peek().Time).TotalSeconds > HistorySeconds)
            history.Dequeue();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;

        DateTime now = DateTime.Now;
        float? cpu = GetCpuTemperature();
        float? gpu = GetGpuTemperature();
        (float? download, float? upload) = GetNetworkThroughput();
        UpdateHistory(_cpuHistory, cpu, now);
        UpdateHistory(_gpuHistory, gpu, now);

        int w = Size.Width;
        int h = Size.Height;

        using (SolidBrush background = new(BackgroundColor))
        using (GraphicsPath path = CreateRoundedRectangle(new Rectangle(0, 0, w - 1, h - 1), 10))
            g.FillPath(background, path);

        int rowHeight = h / 3;
        DrawRow(g, new Rectangle(0, 0, w, rowHeight), "CPU", cpu, _cpuHistory, CpuColor, now);
        DrawRow(g, new Rectangle(0, rowHeight, w, rowHeight), "GPU", gpu, _gpuHistory, GpuColor, now);
        DrawNetworkRow(g, new Rectangle(0, 2 * rowHeight, w, h - 2 * rowHeight), download, upload);
    }

    private void DrawRow(Graphics g, Rectangle area, string label, float? value, Queue<Sample> history, Color color, DateTime now)
    {
        const int pad = 8;

        using (SolidBrush labelBrush = new(LabelColor))
            g.DrawString(label, _labelFont, labelBrush, area.Left + pad, area.Top + pad - 3);

        string text = value.HasValue ? $"{value.Value:F0} °C" : "--";
        using (SolidBrush valueBrush = new(color))
            g.DrawString(text, _valueFont, valueBrush, area.Left + pad - 2, area.Top + pad + 9);

        Rectangle graph = new(area.Left + 84, area.Top + pad, area.Width - 84 - pad, area.Height - 2 * pad);
        DrawSparkline(g, graph, history, color, now);
    }

    private void DrawNetworkRow(Graphics g, Rectangle area, float? download, float? upload)
    {
        const int pad = 8;

        using (SolidBrush labelBrush = new(LabelColor))
            g.DrawString("NET", _labelFont, labelBrush, area.Left + pad, area.Top + pad - 3);

        string text = download.HasValue || upload.HasValue
            ? $"↓{FormatBytesPerSecond(download)}  ↑{FormatBytesPerSecond(upload)}"
            : "--";
        using (SolidBrush valueBrush = new(NetColor))
            g.DrawString(text, _labelFont, valueBrush, area.Left + pad - 2, area.Top + pad + 11);
    }

    private static void DrawSparkline(Graphics g, Rectangle r, Queue<Sample> history, Color color, DateTime now)
    {
        if (r.Width <= 1 || r.Height <= 1)
            return;

        using (Pen axisPen = new(Color.FromArgb(60, 255, 255, 255)))
            g.DrawLine(axisPen, r.Left, r.Bottom, r.Right, r.Bottom);

        List<Sample> window = new();
        float min = float.MaxValue;
        float max = float.MinValue;
        foreach (Sample sample in history)
        {
            if ((now - sample.Time).TotalSeconds > HistorySeconds)
                continue;

            window.Add(sample);
            min = Math.Min(min, sample.Value);
            max = Math.Max(max, sample.Value);
        }

        if (window.Count < 2)
            return;

        // auto-scale to the recent min/max, enforcing a minimum span so small
        // fluctuations are not amplified to fill the whole graph
        float span = max - min;
        if (span < MinSpan)
        {
            float mid = (min + max) / 2f;
            min = mid - MinSpan / 2f;
            max = mid + MinSpan / 2f;
        }
        else
        {
            // small padding so the line does not touch the top/bottom edges
            min -= span * 0.1f;
            max += span * 0.1f;
        }

        float range = max - min;
        List<PointF> points = new();
        foreach (Sample sample in window)
        {
            float age = (float)(now - sample.Time).TotalSeconds;
            float x = r.Right - (age / HistorySeconds) * r.Width;
            float norm = (sample.Value - min) / range;
            float y = r.Bottom - norm * r.Height;
            points.Add(new PointF(x, y));
        }

        using Pen pen = new(color, 1.5f);
        g.DrawLines(pen, points.ToArray());
    }

    private static GraphicsPath CreateRoundedRectangle(Rectangle rect, int radius)
    {
        int d = radius * 2;
        GraphicsPath path = new();
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private readonly struct Sample
    {
        public Sample(DateTime time, float value)
        {
            Time = time;
            Value = value;
        }

        public DateTime Time { get; }
        public float Value { get; }
    }
}
