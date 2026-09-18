using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace AudioSwitcher.Controls;

/// <summary>A section selector that preserves Button input behavior and exposes selection to UI Automation.</summary>
public sealed class SectionButton : Button
{
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(nameof(IsSelected), typeof(bool), typeof(SectionButton), new PropertyMetadata(false, SelectedChanged));
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    protected override AutomationPeer OnCreateAutomationPeer() => new SectionPeer(this);
    private static void SelectedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (UIElementAutomationPeer.FromElement((SectionButton)sender) is SectionPeer peer)
            peer.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, e.OldValue, e.NewValue);
    }
    private sealed class SectionPeer(SectionButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.SelectionItem ? this : base.GetPattern(patternInterface);
        public bool IsSelected => owner.IsSelected;
        public IRawElementProviderSimple SelectionContainer => null!;
        public void Select() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); owner.RaiseEvent(new RoutedEventArgs(ClickEvent)); }
        public void AddToSelection() => Select();
        public void RemoveFromSelection() => throw new InvalidOperationException("One section must remain selected.");
    }
}
