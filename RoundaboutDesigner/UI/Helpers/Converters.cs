using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RoundaboutDesigner.UI; // for ChatRole

namespace RoundaboutDesigner.UI.Helpers
{
    // Maps a 0-100 score to the same red->yellow->green ramp used by the
    // feature-layer renderer, so list badges visually match the map.
    public class ScoreToColorConverter : IValueConverter
    {
        // Anchor colors identical to the Phase 7 BuildScoreColorRamp:
        //  red    (215,48,39)  low
        //  yellow (255,255,191) mid
        //  green  (26,152,80)  high
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double score = 0;
            if (value is double d) score = d;
            else if (value != null) double.TryParse(value.ToString(), out score);

            score = System.Math.Max(0, System.Math.Min(100, score));
            double t = score / 100.0;

            byte r, g, b;
            if (t < 0.5)
            {
                double u = t / 0.5;                       // red -> yellow
                r = Lerp(215, 255, u);
                g = Lerp(48, 255, u);
                b = Lerp(39, 191, u);
            }
            else
            {
                double u = (t - 0.5) / 0.5;               // yellow -> green
                r = Lerp(255, 26, u);
                g = Lerp(255, 152, u);
                b = Lerp(191, 80, u);
            }
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private static byte Lerp(int a, int b, double t)
            => (byte)System.Math.Round(a + (b - a) * t);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
    public class StateToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is RoundaboutDesigner.Models.SourceState s)
            {
                switch (s)
                {
                    case RoundaboutDesigner.Models.SourceState.Done:
                        return new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37)); // green
                    case RoundaboutDesigner.Models.SourceState.Running:
                        return new SolidColorBrush(Color.FromRgb(0x18, 0x5F, 0xA5)); // blue
                    case RoundaboutDesigner.Models.SourceState.Empty:
                        return new SolidColorBrush(Color.FromRgb(0xA8, 0x5C, 0x00)); // amber
                    default:
                        return new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)); // grey
                }
            }
            return Brushes.Gray;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
    public class BoolToStatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => (value is bool b && b)
                ? new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37))   // green
                : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));  // grey
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // Assistant text in one color, user in another, system in grey.
    public class RoleToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ChatRole role)
            {
                switch (role)
                {
                    case ChatRole.Assistant:
                        return new SolidColorBrush(Color.FromRgb(0x18, 0x5F, 0xA5)); // blue
                    case ChatRole.User:
                        return new SolidColorBrush(Color.FromRgb(0x1A, 0x7F, 0x37)); // green
                    default:
                        return new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)); // grey (system)
                }
            }
            return Brushes.Black;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // Assistant messages render larger than user/system.
    public class RoleToFontSizeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return 12.0; // uniform size for all roles
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
    public class NullToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    public class SizeTierToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string tier = value as string;
            return (tier == "Large" || tier == "VeryLarge") ? Visibility.Visible : Visibility.Collapsed;
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}