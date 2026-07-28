// SPDX-FileCopyrightText: 2026 Evan Ruiz and Dir2Site Contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using dir2site.SftpSync.Core;

namespace dir2site.SftpSync.Ui;

public partial class HostKeyPromptView : Window
{
    // Parameterless ctor for the XAML previewer / designer.
    public HostKeyPromptView()
    {
        InitializeComponent();
    }

    public HostKeyPromptView(HostKeyInfo info) : this()
    {
        DataContext = new HostKeyPromptViewModel(this, info);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Builds an <see cref="IHostKeyVerifier"/> that prompts on <paramref name="owner"/> and, when
    /// the user accepts, calls <paramref name="onAccepted"/> so the fingerprint can be persisted.
    /// </summary>
    public static IHostKeyVerifier CreateVerifier(Window owner, Action<HostKeyInfo>? onAccepted = null) =>
        new PromptVerifier(owner, onAccepted);

    /// <remarks>
    /// <see cref="Verify"/> is invoked by SSH.NET on its own connection thread while the caller's
    /// <c>Task.Run</c> is blocked waiting for it, so the prompt is marshalled to the UI thread and
    /// the connection thread waits for the answer. That is safe here precisely because sync work
    /// never runs on the UI thread; calling it from the UI thread would deadlock.
    /// </remarks>
    private sealed class PromptVerifier(Window owner, Action<HostKeyInfo>? onAccepted) : IHostKeyVerifier
    {
        public bool Verify(HostKeyInfo info)
        {
            var answer = new TaskCompletionSource<bool>();
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    answer.SetResult(await new HostKeyPromptView(info).ShowDialog<bool>(owner));
                }
                catch (Exception ex)
                {
                    // Failing to ask counts as not trusting; the exception surfaces to the caller.
                    answer.SetException(ex);
                }
            });

            var accepted = answer.Task.GetAwaiter().GetResult();
            if (accepted) onAccepted?.Invoke(info);
            return accepted;
        }
    }
}
