// SPDX-License-Identifier: BSD-2-Clause

using System;
using static SDL3.SDL;

namespace ClassicUO.Agent
{
    /// <summary>
    /// Hides or shows the game window at runtime.
    ///
    /// FNA creates its window with SDL_WINDOW_HIDDEN and only reveals it from RegisterGame ->
    /// SDL_ShowWindow, which runs in BeforeLoop() - i.e. after Initialize/LoadContent and before
    /// the first Update. So "headless" is simply: hide it again on the first frame, and keep
    /// suppressing Draw. Nothing else about the client changes, which is the point - the CLI is
    /// driving the exact same game loop, world and socket it would drive windowed.
    /// </summary>
    internal static class HeadlessWindow
    {
        private static IntPtr _handle;
        private static bool _applied;

        /// <summary>True while the window is hidden and drawing should be skipped.</summary>
        public static bool IsHidden { get; private set; }

        /// <summary>
        /// Called every frame from GameController.Update. The first call performs the initial hide
        /// if -headless was passed; subsequent calls are a no-op.
        /// </summary>
        public static void EnsureInitialHide(IntPtr windowHandle)
        {
            _handle = windowHandle;

            if (_applied)
            {
                return;
            }

            _applied = true;

            if (CUOEnviroment.Headless)
            {
                Hide();
            }
        }

        public static void Show()
        {
            IsHidden = false;

            if (_handle != IntPtr.Zero)
            {
                SDL_ShowWindow(_handle);
                SDL_RaiseWindow(_handle);
            }
        }

        public static void Hide()
        {
            IsHidden = true;

            if (_handle != IntPtr.Zero)
            {
                SDL_HideWindow(_handle);
            }
        }
    }
}
