using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Threading;

namespace TopstToolkit.App.Controls;

/// <summary>
/// Keeps Korean words whole when text wraps, like CSS <c>word-break: keep-all</c>. WPF may break a line between any two
/// Hangul syllables, so "나옵니다." ends up as "나옵니" with "다." alone on the next line. A ZERO WIDTH NO-BREAK SPACE
/// (U+FEFF) between the letters of a word takes those breaks away: spaces, and signs such as "/" or "·", stay the places
/// to break. All three fonts of the app have the character and draw nothing for it (checked offscreen 2026-11-23; U+2060
/// is missing from D2Coding and Source Sans 3). A word longer than its line is still broken, as TextWrapping="Wrap" does.
/// </summary>
/// <remarks>
/// The text styles bind <see cref="TextProperty"/> to the element's own <c>Text</c> (TextBlock and Run), so every new
/// text, bound or written, comes through here. The joined text is put back with SetCurrentValue, which keeps the
/// element's own binding; text that would flow back to a view model (a two-way binding, the default of Run.Text) is left
/// alone, so a joiner never reaches a project file. The work waits for the dispatcher: a TextBlock built from Runs must
/// not be changed while its content is changing. It runs before the next render, so the unjoined text is never drawn.
/// </remarks>
public static class KeepWords
{
    public const char Joiner = '﻿';

    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(KeepWords), new PropertyMetadata(null, OnTextChanged));

    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    /// <summary>The text with a joiner wherever a line could break inside a Korean word; the same instance when there is none.</summary>
    /// <param name="before">The character in front of the text (the end of the Run before it); a space at the start of a block.</param>
    public static string Join(string text, char before = ' ')
    {
        StringBuilder? joined = null;
        var previous = before;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (NeedsJoiner(previous, c))
            {
                joined ??= new StringBuilder(text.Length + 16).Append(text, 0, i);
                joined.Append(Joiner);
            }
            joined?.Append(c);
            previous = c;
        }
        return joined?.ToString() ?? text;
    }

    /// <summary>A break between <paramref name="a"/> and <paramref name="b"/> would split a word that has Hangul in it.</summary>
    public static bool NeedsJoiner(char a, char b) =>
        a != Joiner && b != Joiner && (IsHangul(a) || IsHangul(b)) && Sticks(a) && Sticks(b);

    private static bool IsHangul(char c) =>
        c is >= '가' and <= '힣' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏';

    /// <summary>Letters, digits and the signs that belong to their word ("20장", "(.bin)입니다", "88.9%의"); not the places a line may break.</summary>
    private static bool Sticks(char c) =>
        !char.IsWhiteSpace(c) && c is not ('/' or '\\' or '-' or '–' or '—' or '·' or '→' or '←' or '|' or '…');

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not string { Length: > 0 } text || ReferenceEquals(Join(text), text))
            return; // nothing to join (also the second call, after the joined text went in)
        switch (d)
        {
            case TextBlock block:
                block.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(block));
                break;
            case Run run:
                run.Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(run));
                break;
        }
    }

    private static void Apply(TextBlock block)
    {
        // a TextBlock built from Runs: each Run joins itself (the Run style), the TextBlock's Text is not the place
        foreach (var child in LogicalTreeHelper.GetChildren(block))
            if (child is Inline)
                return;
        if (WritesBack(block, TextBlock.TextProperty))
            return;
        var text = block.Text ?? "";
        var joined = Join(text);
        if (!ReferenceEquals(joined, text))
            block.SetCurrentValue(TextBlock.TextProperty, joined);
    }

    private static void Apply(Run run)
    {
        if (WritesBack(run, Run.TextProperty))
            return;
        var text = run.Text ?? "";
        var joined = Join(text, CharBefore(run));
        if (!ReferenceEquals(joined, text))
            run.SetCurrentValue(Run.TextProperty, joined);
    }

    /// <summary>The last character in front of a Run, so "라이선스" in a link and "를 확인했고" after it stay one word.</summary>
    private static char CharBefore(Inline inline)
    {
        for (Inline? at = inline; at is not null; at = at.Parent as Inline)
            if (at.PreviousInline is { } previous)
                return LastChar(previous);
        return ' ';
    }

    private static char LastChar(Inline inline) => inline switch
    {
        Run { Text: { Length: > 0 } text } => text[^1],
        Span { Inlines.LastInline: { } last } => LastChar(last),
        _ => ' ',
    };

    /// <summary>The value would flow back to its source: two-way and to-source bindings (Run.Text binds two-way by default), and anything not a plain binding.</summary>
    private static bool WritesBack(DependencyObject element, DependencyProperty property)
    {
        var mode = BindingOperations.GetBindingExpressionBase(element, property) switch
        {
            null => DependencyPropertyHelper.GetValueSource(element, property).IsExpression ? BindingMode.TwoWay : BindingMode.OneWay,
            BindingExpression single => single.ParentBinding.Mode,
            MultiBindingExpression multi => multi.ParentMultiBinding.Mode,
            _ => BindingMode.TwoWay,
        };
        if (mode == BindingMode.Default)
            mode = property.GetMetadata(element) is FrameworkPropertyMetadata { BindsTwoWayByDefault: true } ? BindingMode.TwoWay : BindingMode.OneWay;
        return mode is BindingMode.TwoWay or BindingMode.OneWayToSource;
    }
}
