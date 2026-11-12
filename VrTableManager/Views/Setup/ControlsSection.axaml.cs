using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using VrTableManager.ViewModels;

namespace VrTableManager.Views.Setup;

public partial class ControlsSection : UserControl
{
    TopLevel? _top;

    /// <summary>The key just given to a row: while it's held (key repeat) and when it's let go, it goes nowhere else.</summary>
    PhysicalKey _held = PhysicalKey.None;

    /// <summary>
    /// Alt or a Windows key pressed while a row waits: it's given to the row when it's let go, so Alt+Tab or Win+D
    /// (which take the window's focus away first) don't save it.
    /// </summary>
    PhysicalKey _pendingModifier = PhysicalKey.None;

    MainViewModel? Vm => DataContext as MainViewModel;

    public ControlsSection()
    {
        InitializeComponent();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty)
            {
                return;
            }

            if (IsVisible)
            {
                Vm?.RefreshControls();
            }
            else
            {
                StopWaiting(); // leaving the section stops waiting for a key
            }
        };
    }

    // While a row waits for a key, every key goes to it (tunnelling from the window, before a focused button takes
    // Enter or Space, Tab moves focus, or Alt opens anything), including Escape: VPX's Exit Table key.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top?.AddHandler(KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_top is Window w)
        {
            w.Activated += OnWindowActivated;
            w.Deactivated += OnWindowDeactivated;
        }

        // Back on the Setup tab (other tabs detach it): the keys may have changed meanwhile.
        if (IsVisible)
        {
            Vm?.RefreshControls();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopWaiting();
        _top?.RemoveHandler(KeyDownEvent, OnKeyDown);
        _top?.RemoveHandler(KeyUpEvent, OnKeyUp);
        if (_top is Window w)
        {
            w.Activated -= OnWindowActivated;
            w.Deactivated -= OnWindowDeactivated;
        }

        _top = null;
        _held = PhysicalKey.None;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Leaving the section or the window: no row waits, and VPX's SDL library is let go.</summary>
    void StopWaiting()
    {
        _pendingModifier = PhysicalKey.None;
        Vm?.CancelKeyCapture();
        Vm?.ReleaseControllers();
    }

    /// <summary>Back from a table (VPX's menu may have remapped keys), or from editing an ini by hand.</summary>
    void OnWindowActivated(object? sender, EventArgs e)
    {
        if (IsVisible && Vm is { CapturingRow: null } vm)
        {
            vm.RefreshControls();
        }
    }

    /// <summary>Switching away (Alt+Tab, the Windows key, a click elsewhere) isn't a key for the row.</summary>
    void OnWindowDeactivated(object? sender, EventArgs e)
    {
        StopWaiting();
        _held = PhysicalKey.None;
    }

    static bool IsSwitchModifier(PhysicalKey k) =>
        k
            is PhysicalKey.AltLeft
                or PhysicalKey.AltRight
                or PhysicalKey.MetaLeft
                or PhysicalKey.MetaRight;

    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.PhysicalKey == PhysicalKey.None)
        {
            return;
        }

        // The key just set, repeating while it's held: not a click on the focused SET button.
        if (e.PhysicalKey == _held)
        {
            e.Handled = true;
            return;
        }

        if (!IsVisible || Vm is not { CapturingRow: not null } vm)
        {
            return;
        }

        e.Handled = true;
        if (IsSwitchModifier(e.PhysicalKey))
        {
            _pendingModifier = e.PhysicalKey; // decided when it's let go
            return;
        }

        // Another key while Alt or Windows is down (Alt+Enter…): the other key is the one meant.
        _pendingModifier = PhysicalKey.None;
        vm.KeyPressedForCapture(e.PhysicalKey.ToString());
        _held = e.PhysicalKey;
    }

    void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (_pendingModifier != PhysicalKey.None && e.PhysicalKey == _pendingModifier)
        {
            _pendingModifier = PhysicalKey.None;
            e.Handled = true;
            if (IsVisible && Vm is { } vm)
            {
                vm.KeyPressedForCapture(e.PhysicalKey.ToString());
            }

            return;
        }

        if (_held != PhysicalKey.None && e.PhysicalKey == _held)
        {
            e.Handled = true;
            _held = PhysicalKey.None;
        }
    }
}
