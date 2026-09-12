using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Dream.Studio.Docking
{
    /// <summary>
    /// A single tab header inside a <see cref="DockTabGroup"/>. Renders the panel title and an
    /// optional close button, and surfaces <see cref="RequestClose"/> and
    /// <see cref="DoubleTapped"/> as CLR events so the owning group can react without knowing the
    /// template internals.
    /// </summary>
    public sealed class DockTabHeader : TemplatedControl
    {
        public static readonly StyledProperty<string> TitleProperty =
            AvaloniaProperty.Register<DockTabHeader, string>(nameof(Title), string.Empty);

        public static readonly StyledProperty<bool> IsActiveProperty =
            AvaloniaProperty.Register<DockTabHeader, bool>(nameof(IsActive));

        public static readonly StyledProperty<bool> CanCloseProperty =
            AvaloniaProperty.Register<DockTabHeader, bool>(nameof(CanClose), true);

        private const int DoubleTapMs = 400;

        private Button? _close;
        private DateTime _lastTap = DateTime.MinValue;

        static DockTabHeader()
        {
            IsActiveProperty.Changed.AddClassHandler<DockTabHeader>((h, _) => h.UpdatePseudoClass());
        }

        public DockTabHeader()
        {
            UpdatePseudoClass();
        }

        public string Title
        {
            get => GetValue(TitleProperty);
            set => SetValue(TitleProperty, value);
        }

        public bool IsActive
        {
            get => GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public bool CanClose
        {
            get => GetValue(CanCloseProperty);
            set => SetValue(CanCloseProperty, value);
        }

        /// <summary>Raised when the user clicks the close button.</summary>
        public event EventHandler<EventArgs>? RequestClose;

        /// <summary>Raised on a quick second press (used to float/dock-toggle a panel).</summary>
        public new event EventHandler<EventArgs>? DoubleTapped;

        protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
        {
            base.OnApplyTemplate(e);

            if (_close is not null)
            {
                _close.Click -= OnCloseClicked;
                _close.PointerPressed -= OnClosePressed;
            }

            _close = e.NameScope?.Find("PART_Close") as Button;

            if (_close is not null)
            {
                _close.Click += OnCloseClicked;
                // Swallow the press so it neither starts a tab drag nor selects.
                _close.PointerPressed += OnClosePressed;
            }
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                base.OnPointerPressed(e);
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - _lastTap).TotalMilliseconds < DoubleTapMs)
            {
                // 双击命中：不调用 base.OnPointerPressed，避免触发 PointerPressed CLR 事件
                // → OnHeaderPressed → BeginDrag（捕获指针）。否则后续 ToggleFloat 同步销毁
                // 可视树 + 窗口 Close 会导致指针捕获悬空崩溃。标记 Handled 阻止默认处理。
                _lastTap = DateTime.MinValue;
                e.Handled = true;
                DoubleTapped?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _lastTap = now;
                base.OnPointerPressed(e);
            }
        }

        private void OnCloseClicked(object? sender, RoutedEventArgs e) => RequestClose?.Invoke(this, EventArgs.Empty);

        private void OnClosePressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

        private void UpdatePseudoClass() => PseudoClasses.Set(":selected", IsActive);
    }
}
