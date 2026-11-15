using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace L_Mechrevo.WpfP0.Services;

/// <summary>
/// 快捷开关图标的唯一构造入口：磁贴、迷你窗、选择框共用同一套图形与尺寸，
/// 避免各处自绘导致"同一开关三种样子"。
/// </summary>
internal static class QuickIconFactory
{
	/// <summary>图标宿主尺寸（宽度稍大，给 Num / Fn 这类文字图标留位）。</summary>
	internal const double HostWidth = 20;

	/// <summary>文字图标（Num / Fn / Aa）的宿主宽度：比矢量图标宽一点，避免右侧被压。</summary>
	internal const double HostWidthForText = 22;

	internal const double HostHeight = 18;

	internal static FrameworkElement Create(string? key, string? glyph)
	{
		FrameworkElement inner = CreateInner(key, glyph);
		Grid host = new()
		{
			Width = QuickGlyphs.IsText(glyph) ? HostWidthForText : HostWidth,
			Height = HostHeight,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			Background = Brushes.Transparent
		};
		inner.HorizontalAlignment = HorizontalAlignment.Center;
		inner.VerticalAlignment = VerticalAlignment.Center;
		host.Children.Add(inner);
		return host;
	}

	/// <summary>风扇标（强冷）：磁贴与 OSD 共用同一几何。</summary>
	internal static FrameworkElement CreateFanShape(Brush? fill = null)
	{
		Path fan = new()
		{
			Data = Geometry.Parse(
				"M8,8 C6,5 6,2 8,1 C10,2 10,5 8,8 Z M8,8 C11,6 14,7 15,9 C13,11 10,10 8,8 Z "
				+ "M8,8 C10,11 10,14 8,15 C6,14 6,11 8,8 Z M8,8 C5,10 2,9 1,7 C3,5 6,6 8,8 Z "
				+ "M8,7 A1,1 0 1 1 8,9 A1,1 0 1 1 8,7 Z"),
			StrokeThickness = 0
		};
		if (fill != null) 		{ 			fan.Fill = fill; 		} 		else 		{ 			fan.SetResourceReference(Shape.FillProperty, "TextBrush"); 		}
		return WrapInCanvas(fan);
	}

	/// <summary>Windows 四方格标（禁用 Win 键）：四个圆角方块。</summary>
	internal static FrameworkElement CreateWindowsLogo(double size, Brush? fill = null)
	{
		double gap = size * 0.09;
		double cell = (size - gap) / 2;
		double radius = Math.Max(1, size * 0.08);
		Grid logo = new()
		{
			Width = size,
			Height = size,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		for (int i = 0; i < 4; i++)
		{
			Rectangle box = new()
			{
				Width = cell,
				Height = cell,
				RadiusX = radius,
				RadiusY = radius,
				HorizontalAlignment = i % 2 == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
				VerticalAlignment = i < 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom
			};
			if (fill != null) 			{ 				box.Fill = fill; 			} 			else 			{ 				box.SetResourceReference(Shape.FillProperty, "TextBrush"); 			}
			logo.Children.Add(box);
		}
		return logo;
	}

	/// <summary>OSD 专用矢量（深色遮罩上用白色）：风扇 / Windows 四方格，按给定尺寸缩放。</summary>
	internal static FrameworkElement? CreateForOsd(string? marker, double size)
	{
		FrameworkElement? shape = marker switch
		{
			"fanboost" => CreateFanShape(Brushes.White),
			"winlogo" => CreateWindowsLogo(16, Brushes.White),
			_ => null
		};
		if (shape == null)
		{
			return null;
		}
		shape.HorizontalAlignment = HorizontalAlignment.Center;
		shape.VerticalAlignment = VerticalAlignment.Center;
		Viewbox box = new()
		{
			Width = size,
			Height = size,
			Stretch = Stretch.Uniform,
			StretchDirection = StretchDirection.Both,
			Child = shape
		};
		return box;
	}

	private static FrameworkElement CreateInner(string? key, string? glyph)
	{
		if (string.Equals(key, "fanboost", StringComparison.OrdinalIgnoreCase))
		{
			return CreateFanShape();
		}

		if (string.Equals(key, "winkey", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(glyph, "winlogo", StringComparison.OrdinalIgnoreCase))
		{
			return CreateWindowsLogo(16);
		}

		if (string.Equals(key, "touchpad", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(key, "touchpadtoggle", StringComparison.OrdinalIgnoreCase))
		{
			Path outline = new()
			{
				Data = Geometry.Parse("M3,3.5 L13,3.5 L13,12.5 L3,12.5 Z"),
				StrokeThickness = 1.3,
				Fill = Brushes.Transparent
			};
			Path bar = new()
			{
				Data = Geometry.Parse("M6,10 L10,10"),
				StrokeThickness = 1.3
			};
			outline.SetResourceReference(Shape.StrokeProperty, "TextBrush");
			bar.SetResourceReference(Shape.StrokeProperty, "TextBrush");
			return WrapInCanvas(outline, bar);
		}

		if (string.Equals(key, "copilot", StringComparison.OrdinalIgnoreCase))
		{
			ImageBrush mask = new(new BitmapImage(
				new Uri("pack://application:,,,/L-Mechrevo;component/Assets/Icons/copilot-line.png")))
			{
				Stretch = Stretch.Uniform
			};
			Rectangle rect = new()
			{
				Width = 15,
				Height = 14,
				OpacityMask = mask
			};
			rect.SetResourceReference(Shape.FillProperty, "TextBrush");
			return rect;
		}

		// 文字图标（Num / Fn / Aa 等）：图标字体没有这些字符，必须用正文字体，否则是方框。
		if (QuickGlyphs.IsText(glyph))
		{
			TextBlock text = new()
			{
				Text = glyph,
				FontFamily = QuickGlyphs.Font(glyph),
				FontSize = QuickGlyphs.Size(glyph),
				FontWeight = QuickGlyphs.Weight(glyph),
				TextAlignment = TextAlignment.Center,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
			// 文字比宿主宽时等比缩小（只缩不放）：不同字体/DPI/字符数都不会再被裁。
			return new Viewbox
			{
				Stretch = Stretch.Uniform,
				StretchDirection = StretchDirection.DownOnly,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center,
				Child = text
			};
		}

		TextBlock icon = new()
		{
			Text = glyph ?? string.Empty,
			FontFamily = QuickGlyphs.Font(glyph),
			FontSize = QuickGlyphs.Size(glyph),
			FontWeight = QuickGlyphs.Weight(glyph),
			TextAlignment = TextAlignment.Center,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
		icon.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
		return icon;
	}

	private static FrameworkElement WrapInCanvas(params UIElement[] children)
	{
		Canvas canvas = new() { Width = 16, Height = 16 };
		foreach (UIElement child in children)
		{
			canvas.Children.Add(child);
		}
		return new Viewbox
		{
			Width = 16,
			Height = 16,
			Child = canvas,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center
		};
	}
}
