using System;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Windowing;

namespace XrayUI.Helpers
{
    /// <summary>
    /// Turns a secondary <see cref="Window"/> into a modal dialog owned by another window.
    ///
    /// Extracted because the sequence is order-dependent in ways nothing in the API signals, and
    /// getting it wrong fails silently — the window opens, just not modal. Every tool window in
    /// the app needs the identical dance, so it lives here rather than being copied with its
    /// explanatory comments into each one.
    /// </summary>
    public static class ModalWindowHelper
    {
        private const int GWLP_HWNDPARENT = -8;

        [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLongPtr")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("User32.dll", CharSet = CharSet.Auto, EntryPoint = "SetWindowLong")]
        private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        /// <summary>
        /// Makes <paramref name="window"/> a modal dialog owned by <paramref name="owner"/> and
        /// shows it. Call after InitializeComponent and after the theme/backdrop are applied.
        /// </summary>
        public static void ShowAsOwnedModal(this Window window, Window owner)
        {
            var presenter = OverlappedPresenter.CreateForDialog();

            // 1. Set the Win32 owner BEFORE IsModal — IsModal requires an owner.
            SetWindowOwner(window, owner);

            // 2. Mark the presenter modal, then commit it to the AppWindow.
            presenter.IsModal = true;
            window.AppWindow.SetPresenter(presenter);

            // 3. Show via AppWindow.Show() to apply the modal presenter at the OS level.
            //    Window.Activate() doesn't reliably re-apply IsModal once the window
            //    has any prior presenter state.
            window.AppWindow.Show();
        }

        private static void SetWindowOwner(Window window, Window owner)
        {
            var ownerHwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
            var ownedHwnd = Win32Interop.GetWindowFromWindowId(window.AppWindow.Id);

            if (IntPtr.Size == 8)
                SetWindowLongPtr(ownedHwnd, GWLP_HWNDPARENT, ownerHwnd);
            else
                SetWindowLong(ownedHwnd, GWLP_HWNDPARENT, ownerHwnd);
        }
    }
}
