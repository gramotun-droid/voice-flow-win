using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using VoiceFlowWin.Core.Abstractions;
using VoiceFlowWin.Windows.System;

namespace VoiceFlowWin.App.Services;

/// <summary>
/// Находит каретку быстрым Win32-способом, а для современных редакторов
/// использует Windows UI Automation.
/// </summary>
public sealed class CaretPositionProvider : ICaretPositionProvider
{
    private readonly ForegroundFocusTracker _win32;

    public CaretPositionProvider(ForegroundFocusTracker win32) => _win32 = win32;

    public bool TryGetCaretBounds(out CaretScreenBounds bounds)
    {
        if (_win32.TryGetCaretBounds(out bounds))
        {
            return true;
        }

        // Chromium, Electron и часть современных редакторов рисуют каретку
        // сами: hwndCaret у них равен нулю. TextPattern позволяет найти её по
        // диапазону выделения активного элемента.
        return TryGetAutomationCaretBounds(out bounds);
    }

    private static bool TryGetAutomationCaretBounds(out CaretScreenBounds bounds)
    {
        bounds = default;

        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return false;
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
            {
                return TryGetAutomationFieldFallbackBounds(focused, out bounds);
            }

            var selection = pattern.GetSelection();
            if (selection.Length == 0)
            {
                return false;
            }

            var caret = selection[0];
            var isDegenerate = caret.CompareEndpoints(
                TextPatternRangeEndpoint.Start,
                caret,
                TextPatternRangeEndpoint.End) == 0;

            // TextPattern не возвращает прямоугольник для диапазона нулевой
            // длины. Расширяем копию на один соседний символ и берём его край.
            var probe = caret.Clone();
            var useRightEdge = false;
            var emptyText = false;
            if (isDegenerate)
            {
                var movedForward = probe.MoveEndpointByUnit(
                    TextPatternRangeEndpoint.End,
                    TextUnit.Character,
                    1);

                if (movedForward == 0)
                {
                    var movedBackward = probe.MoveEndpointByUnit(
                        TextPatternRangeEndpoint.Start,
                        TextUnit.Character,
                        -1);
                    useRightEdge = movedBackward != 0;
                    emptyText = movedBackward == 0;
                }
            }

            var rectangles = probe.GetBoundingRectangles();
            if (rectangles.Length == 0)
            {
                return emptyText && TryGetAutomationFieldFallbackBounds(focused, out bounds);
            }

            var rectangle = useRightEdge ? rectangles[^1] : rectangles[0];
            if (rectangle.IsEmpty || rectangle.Height <= 0)
            {
                return false;
            }

            var x = useRightEdge ? rectangle.Right : rectangle.Left;
            bounds = new CaretScreenBounds(
                (int)Math.Round(x),
                (int)Math.Round(rectangle.Top),
                (int)Math.Round(x),
                (int)Math.Round(rectangle.Bottom));
            return true;
        }
        catch (Exception exception) when (exception is ElementNotAvailableException or
                                           InvalidOperationException or
                                           COMException or
                                           ArgumentException)
        {
            // Элемент мог исчезнуть между получением фокуса и чтением range.
            return false;
        }
    }

    private static bool TryGetAutomationFieldFallbackBounds(
        AutomationElement focused,
        out CaretScreenBounds bounds)
    {
        bounds = default;

        var controlType = focused.Current.ControlType;
        if (controlType != ControlType.Edit && controlType != ControlType.Document)
        {
            return false;
        }

        var field = focused.Current.BoundingRectangle;
        if (field.IsEmpty || field.Width <= 0 || field.Height <= 0)
        {
            return false;
        }

        // Без TextPattern точной горизонтальной координаты нет. Показываем
        // индикатор у внутреннего края активного поля вместо полного исчезновения.
        var left = (int)Math.Round(field.Left + 6);
        var top = (int)Math.Round(field.Top + 4);
        var bottom = (int)Math.Round(Math.Min(field.Bottom - 4, field.Top + 24));
        if (bottom <= top)
        {
            return false;
        }

        bounds = new CaretScreenBounds(left, top, left, bottom);
        return true;
    }
}
