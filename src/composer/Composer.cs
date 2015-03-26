﻿//
//  WinCompose — a compose key for Windows — http://wincompose.info/
//
//  Copyright © 2013—2015 Sam Hocevar <sam@hocevar.net>
//              2014—2015 Benjamin Litzelmann
//
//  This program is free software. It comes without any warranty, to
//  the extent permitted by applicable law. You can redistribute it
//  and/or modify it under the terms of the Do What the Fuck You Want
//  to Public License, Version 2, as published by the WTFPL Task Force.
//  See http://www.wtfpl.net/ for more details.
//

using System;
using System.Collections.Generic;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WinCompose
{

static class Composer
{
    /// <summary>
    /// Initialize the composer.
    /// </summary>
    public static void Init()
    {
        AnalyzeDeadkeys();
    }

    /// <summary>
    /// Terminate the composer.
    /// </summary>
    public static void Fini()
    {
    }

    /// <summary>
    /// Get input from the keyboard hook; return true if the key was handled
    /// and needs to be removed from the input chain.
    /// </summary>
    public static bool OnKey(WM ev, VK vk, SC sc, LLKHF flags)
    {
        // Remember when the user touched a key for the last time
        m_last_key_time = DateTime.Now;

        // Do nothing if we are disabled; NOTE: this disables stats, too
        if (m_disabled)
        {
            return false;
        }

        int dead_key = SaveDeadKey();
        bool ret = OnKeyInternal(ev, vk, sc, flags);
        RestoreDeadKey(dead_key);

        return ret;
    }

    private static bool OnKeyInternal(WM ev, VK vk, SC sc, LLKHF flags)
    {
        CheckKeyboardLayout();

        bool is_keydown = (ev == WM.KEYDOWN || ev == WM.SYSKEYDOWN);
        bool is_keyup = !is_keydown;

        bool has_shift = (NativeMethods.GetKeyState(VK.SHIFT) & 0x80) != 0;
        bool has_altgr = (NativeMethods.GetKeyState(VK.LCONTROL) &
                          NativeMethods.GetKeyState(VK.RMENU) & 0x80) != 0;
        bool has_lrshift = (NativeMethods.GetKeyState(VK.LSHIFT) &
                            NativeMethods.GetKeyState(VK.RSHIFT) & 0x80) != 0;
        bool has_capslock = NativeMethods.GetKeyState(VK.CAPITAL) != 0;

        // Guess what key was just pressed. If we can not find a printable
        // representation for the key, default to its virtual key code.
        Key key;

        byte[] keystate = new byte[256];
        NativeMethods.GetKeyboardState(keystate);
        keystate[(int)VK.SHIFT] = (byte)(has_shift ? 0x80 : 0x00);
        keystate[(int)VK.CONTROL] = (byte)(has_altgr ? 0x80 : 0x00);
        keystate[(int)VK.MENU] = (byte)(has_altgr ? 0x80 : 0x00);
        keystate[(int)VK.CAPITAL] = (byte)(has_capslock ? 0x01 : 0x00);

        string str_if_normal = KeyToUnicode(vk, sc, keystate, flags);
        string str_if_dead = KeyToUnicode(VK.SPACE);
        if (str_if_normal != "")
        {
            // This appears to be a normal, printable key
            key = new Key(str_if_normal);
        }
        else if (str_if_dead != " ")
        {
            // This appears to be a dead key
            key = new Key(str_if_dead);
        }
        else
        {
            // This appears to be a non-printable, non-dead key
            key = new Key(vk);
        }

        if (is_keydown)
        {
            // Update single key statistics
            Stats.AddKey(key);

            // Update key pair statistics if applicable
            if (DateTime.Now < m_last_key_time.AddMilliseconds(2000)
                 && m_last_key != null)
            {
                Stats.AddPair(m_last_key, key);
            }

            // Remember when we pressed a key for the last time
            m_last_key_time = DateTime.Now;
            m_last_key = key;
        }

        Log("Key {0}: {1}", is_keydown ? "Down" : "Up", key.FriendlyName);

        // Special case: we don't consider characters such as Esc as printable
        // otherwise they are not properly serialised in the config file.
        if (key.IsPrintable() && key.ToString()[0] < ' ')
        {
            key = new Key(vk);
        }

        // FIXME: we don’t properly support compose keys that also normally
        // print stuff, such as `.
        if (key == Settings.ComposeKey.Value)
        {
            if (is_keyup)
            {
                m_compose_down = false;
            }
            else if (is_keydown && !m_compose_down)
            {
                // FIXME: we don't want compose + compose to disable composing,
                // since there are compose sequences that use Multi_key.
                // FIXME: also, if a sequence was in progress, print it!
                m_compose_down = true;
                m_composing = !m_composing;
                if (!m_composing)
                    m_sequence.Clear();

                // Lauch the sequence reset expiration thread
                // FIXME: do we need to launch a new thread each time the
                // compose key is pressed? Let's have a dormant thread instead
                if (m_composing && Settings.ResetDelay.Value > 0)
                {
                    new Thread(() =>
                    {
                        while (m_composing && DateTime.Now < m_last_key_time.AddMilliseconds(Settings.ResetDelay.Value))
                            Thread.Sleep(50);
                        ResetSequence();
                    }).Start();
                }
            }

            if (Changed != null)
                Changed(null, new EventArgs());

            return true;
        }

        // Feature: emulate capslock key with both shift keys, and optionally
        // disable capslock using only one shift key.
        if (key.VirtualKey == VK.LSHIFT || key.VirtualKey == VK.RSHIFT)
        {
            if (is_keyup && has_lrshift && Settings.EmulateCapsLock.Value)
            {
                SendKeyPress(VK.CAPITAL);
                return false;
            }

            if (is_keydown && has_capslock && Settings.ShiftDisablesCapsLock.Value)
            {
                SendKeyPress(VK.CAPITAL);
                return false;
            }
        }

        // If we are not currently composing a sequence, do nothing. But if
        // this was a dead key, eat it.
        if (!m_composing)
        {
            return false;
        }

        if (!Settings.IsUsableKey(key))
        {
            // FIXME: if compose key is down, maybe there is a key combination
            // going on, such as Alt+Tab or Windows+Up
            return false;
        }

        // Finally, everything else ignored this key, so it is a key we must
        // add to our compose sequence.
        if (is_keydown)
        {
            AddToSequence(key);
            return true;
        }

        return true;
    }

    /// <summary>
    /// Add a key to the sequence currently being built. If necessary, output
    /// the finished sequence or trigger actions for invalid sequences.
    /// </summary>
    private static void AddToSequence(Key key)
    {
        // FIXME: we don’t support case-insensitive yet
        // We try the following, in this order:
        //  1. if m_sequence + key is a valid prefix, it means the user
        //     could type other characters to build a longer sequence,
        //     so just append key to m_sequence.
        //  2. if m_sequence + key is a valid sequence, we can't go further,
        //     we append key to m_sequence and output the result.
        //  3. if m_sequence is a valid sequence, the user didn't type a
        //     valid key, so output the m_sequence result _and_ process key.
        //  4. (optionally) try again 1. 2. and 3. ignoring case.
        //  5. none of the characters make sense, output all of them as if
        //     the user didn't press Compose.
        foreach (bool ignore_case in Settings.CaseInsensitive.Value ?
                              new bool[]{ false, true } : new bool[]{ false })
        {
            KeySequence old_sequence = new KeySequence(m_sequence);
            m_sequence.Add(key);

            if (Settings.IsValidPrefix(m_sequence))
            {
                // Still a valid prefix, continue building sequence
                return;
            }

            if (Settings.IsValidSequence(m_sequence))
            {
                string tosend = Settings.GetSequenceResult(m_sequence);
                Stats.AddSequence(m_sequence);
                ResetSequence();
                SendString(tosend);
                return;
            }

            if (Settings.IsValidSequence(old_sequence))
            {
                string tosend = Settings.GetSequenceResult(old_sequence);
                Stats.AddSequence(old_sequence);
                ResetSequence();
                SendString(tosend);
                if (key.IsPrintable())
                    SendString(key.ToString());
                return;
            }
        }

        // Unknown characters for sequence, print them if necessary
        if (!Settings.DiscardOnInvalid.Value)
        {
            foreach (Key k in m_sequence)
            {
                // FIXME: what if the key is e.g. left arrow?
                if (k.IsPrintable())
                    SendString(k.ToString());
            }
        }

        if (Settings.BeepOnInvalid.Value)
            SystemSounds.Beep.Play();

        ResetSequence();
    }

    private static string KeyToUnicode(VK vk)
    {
        return KeyToUnicode(vk, (SC)0, new byte[256], (LLKHF)0);
    }

    private static string KeyToUnicode(VK vk, SC sc, byte[] keystate, LLKHF flags)
    {
        const int buflen = 4;
        byte[] buf = new byte[2 * buflen];
        int ret = NativeMethods.ToUnicode(vk, sc, keystate, buf, buflen, flags);
        if (ret > 0 && ret < buflen)
        {
            return Encoding.Unicode.GetString(buf, 0, ret * 2);
        }
        return "";
    }

    private static void SendString(string str)
    {
        List<VK> modifiers = new List<VK>();
        bool use_gtk_hack = false, use_office_hack = false;

        const int len = 256;
        StringBuilder buf = new StringBuilder(len);
        var hwnd = NativeMethods.GetForegroundWindow();
        if (NativeMethods.GetClassName(hwnd, buf, len) > 0)
        {
            string wclass = buf.ToString();

            /* HACK: GTK+ applications behave differently with Unicode, and some
             * applications such as XChat for Windows rename their own top-level
             * window, so we parse through the names we know in order to detect
             * a GTK+ application. */
            if (wclass == "gdkWindowToplevel" || wclass == "xchatWindowToplevel")
                use_gtk_hack = true;

            /* HACK: in MS Office, some symbol insertions change the text font
             * without returning to the original font. To avoid this, we output
             * a space character, then go left, insert our actual symbol, then
             * go right and backspace. */
            /* These are the actual window class names for Outlook and Word…
             * TODO: PowerPoint ("PP(7|97|9|10)FrameClass") */
            if (wclass == "rctrl_renwnd32" || wclass == "OpusApp")
                use_office_hack = true && Settings.InsertZwsp.Value;
        }

        /* Clear keyboard modifiers if we need one of our custom hacks */
        if (use_gtk_hack || use_office_hack)
        {
            VK[] all_modifiers = new VK[]
            {
                VK.LSHIFT, VK.RSHIFT,
                VK.LCONTROL, VK.RCONTROL,
                VK.LMENU, VK.RMENU,
            };

            foreach (VK vk in all_modifiers)
                if ((NativeMethods.GetKeyState(vk) & 0x80) == 0x80)
                    modifiers.Add(vk);

            foreach (VK vk in modifiers)
                SendKeyUp(vk);
        }

        if (use_gtk_hack)
        {
            /* Wikipedia says Ctrl+Shift+u, release, then type the four hex
             * digits, and press Enter.
             * (http://en.wikipedia.org/wiki/Unicode_input). */
            SendKeyDown(VK.LCONTROL);
            SendKeyDown(VK.LSHIFT);
            SendKeyPress((VK)'U');
            SendKeyUp(VK.LSHIFT);
            SendKeyUp(VK.LCONTROL);

            foreach (var ch in str)
                foreach (var key in String.Format("{0:X04} ", (short)ch))
                    SendKeyPress((VK)key);
        }
        else
        {
            List<INPUT> input = new List<INPUT>();

            if (use_office_hack)
            {
                input.Add(NewInputKey((ScanCodeShort)'\u200b'));
                input.Add(NewInputKey((VirtualKeyShort)VK.LEFT));
            }

            for (int i = 0; i < str.Length; i++)
            {
                input.Add(NewInputKey((ScanCodeShort)str[i]));
            }

            if (use_office_hack)
            {
                input.Add(NewInputKey((VirtualKeyShort)VK.RIGHT));
            }

            NativeMethods.SendInput((uint)input.Count, input.ToArray(),
                                    Marshal.SizeOf(typeof(INPUT)));
        }

        /* Restore keyboard modifiers if we needed one of our custom hacks */
        if (use_gtk_hack || use_office_hack)
        {
            foreach (VK vk in modifiers)
                SendKeyDown(vk);
        }
    }

    public static event EventHandler Changed;

    /// <summary>
    /// Return whether a compose sequence is in progress
    /// </summary>
    public static bool IsComposing()
    {
        return m_composing;
    }

    /// <summary>
    /// Toggle the disabled state
    /// </summary>
    public static void ToggleDisabled()
    {
        m_disabled = !m_disabled;

        if (m_disabled)
        {
            m_composing = false;
            m_compose_down = false;
            m_sequence.Clear();
        }

        if (Changed != null)
            Changed(null, new EventArgs());
    }

    /// <summary>
    /// Return whether WinCompose has been disabled
    /// </summary>
    public static bool IsDisabled()
    {
        return m_disabled;
    }

    private static void ResetSequence()
    {
        // FIXME: clear dead key context?

        m_composing = false;
        m_compose_down = false;
        m_sequence.Clear();

        if (Changed != null)
            Changed(null, new EventArgs());
    }

    private static INPUT NewInputKey(VirtualKeyShort vk)
    {
        INPUT ret = NewInputKey();
        ret.U.ki.wVk = vk;
        return ret;
    }

    private static INPUT NewInputKey(ScanCodeShort sc)
    {
        INPUT ret = NewInputKey();
        ret.U.ki.wScan = sc;
        return ret;
    }

    private static INPUT NewInputKey()
    {
        INPUT ret = new INPUT();
        ret.type = EINPUT.KEYBOARD;
        ret.U.ki.wVk = (VirtualKeyShort)0;
        ret.U.ki.wScan = (ScanCodeShort)0;
        ret.U.ki.time = 0;
        ret.U.ki.dwFlags = KEYEVENTF.UNICODE;
        ret.U.ki.dwExtraInfo = UIntPtr.Zero;
        return ret;
    }

    private static void SendKeyDown(VK vk)
    {
        NativeMethods.keybd_event(vk, 0, 0, 0);
    }

    private static void SendKeyUp(VK vk)
    {
        NativeMethods.keybd_event(vk, 0, KEYEVENTF.KEYUP, 0);
    }

    private static void SendKeyPress(VK vk)
    {
        SendKeyDown(vk);
        SendKeyUp(vk);
    }

    private static void AnalyzeDeadkeys()
    {
        m_possible_dead_keys = new Dictionary<string, int>();

        // Try every keyboard key followed by space to see which ones are
        // dead keys. This way, when later we want to know if a dead key is
        // currently buffered, we just call ToUnicode(VK.SPACE) and match
        // the result with what we found here.
        byte[] state = new byte[256];

        for (int i = 0; i < 0x400; ++i)
        {
            VK vk = (VK)(i & 0xff);
            bool has_shift = (i & 0x100) != 0;
            bool has_altgr = (i & 0x200) != 0;

            state[(int)VK.SHIFT] = (byte)(has_shift ? 0x80 : 0x00);
            state[(int)VK.CONTROL] = (byte)(has_altgr ? 0x80 : 0x00);
            state[(int)VK.MENU] = (byte)(has_altgr ? 0x80 : 0x00);

            // First the key we’re interested in, then the space key
            KeyToUnicode(vk, (SC)0, state, (LLKHF)0);
            string result = KeyToUnicode(VK.SPACE);

            // If the resulting string is not the space character, it means
            // that it was a dead key. Good!
            if (result != " ")
                m_possible_dead_keys[result] = i;
        }

        // Clean up key buffer
        KeyToUnicode(VK.SPACE);
        KeyToUnicode(VK.SPACE);
    }

    /// <summary>
    /// Save the dead key if there is one, since we'll be calling ToUnicode
    /// later on. This effectively removes any dead key from the ToUnicode
    /// internal buffer.
    /// </summary>
    private static int SaveDeadKey()
    {
        int dead_key = 0;
        string str = KeyToUnicode(VK.SPACE);
        if (str != " ")
            m_possible_dead_keys.TryGetValue(str, out dead_key);
        return dead_key;
    }

    /// <summary>
    /// Restore a previously saved dead key. This should restore the ToUnicode
    /// internal buffer.
    /// </summary>
    private static void RestoreDeadKey(int dead_key)
    {
        if (dead_key == 0)
            return;

        byte[] state = new byte[256];

        VK vk = (VK)(dead_key & 0xff);
        bool has_shift = (dead_key & 0x100) != 0;
        bool has_altgr = (dead_key & 0x200) != 0;

        state[(int)VK.SHIFT] = (byte)(has_shift ? 0x80 : 0x00);
        state[(int)VK.CONTROL] = (byte)(has_altgr ? 0x80 : 0x00);
        state[(int)VK.MENU] = (byte)(has_altgr ? 0x80 : 0x00);

        KeyToUnicode(vk, (SC)0, state, (LLKHF)0);
    }

    // FIXME: this is useless for now
    private static void CheckKeyboardLayout()
    {
        // FIXME: the foreground window doesn't seem to notice keyboard
        // layout changes caused by the Win+Space combination.
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        uint pid, tid = NativeMethods.GetWindowThreadProcessId(hwnd, out pid);
        IntPtr active_layout = NativeMethods.GetKeyboardLayout(tid);
        //Console.WriteLine("Active layout is {0:X}", (int)active_layout);

        tid = NativeMethods.GetCurrentThreadId();
        IntPtr my_layout = NativeMethods.GetKeyboardLayout(tid);
        //Console.WriteLine("WinCompose layout is {0:X}", (int)my_layout);
    }

    private static void Log(string format, params object[] args)
    {
#if DEBUG
        string msg = string.Format("{0} {1}", DateTime.Now,
                                   string.Format(format, args));
        System.Diagnostics.Debug.WriteLine(msg);
#endif
    }

    /// <summary>
    /// The sequence being currently typed
    /// </summary>
    private static KeySequence m_sequence = new KeySequence();

    private static Key m_last_key;
    private static DateTime m_last_key_time = DateTime.Now;
    private static Dictionary<string, int> m_possible_dead_keys;

    private static bool m_disabled = false;
    private static bool m_compose_down = false;
    private static bool m_composing = false;
}

}
