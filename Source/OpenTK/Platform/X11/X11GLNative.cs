#region License
//
// The Open Toolkit Library License
//
// Copyright (c) 2006 - 2009 the Open Toolkit library.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights to 
// use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
// the Software, and to permit persons to whom the Software is furnished to do
// so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
//
#endregion

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
#if !MINIMAL
using System.Drawing;
#endif
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using OpenTK.Graphics;
using OpenTK.Input;

namespace OpenTK.Platform.X11
{
    /// \internal
    /// <summary>
    /// Drives GameWindow on X11.
    /// This class supports OpenTK, and is not intended for use by OpenTK programs.
    /// </summary>
    internal sealed class X11GLNative : INativeWindow, IDisposable
    {
        // TODO: Disable screensaver.
        // TODO: What happens if we can't disable decorations through motif?
        // TODO: Mouse/keyboard grabbing/wrapping.

        #region Fields
        
        const int _min_width = 30, _min_height = 30;

        X11WindowInfo window = new X11WindowInfo();

        // Legacy input support
        X11Input driver;
        KeyboardDevice keyboard;
        MouseDevice mouse;

        // Window manager hints for fullscreen windows.
        // Not used right now (the code is written, but is not 64bit-correct), but could be useful for older WMs which
        // are not ICCM compliant, but may support MOTIF hints.
        const string MOTIF_WM_ATOM = "_MOTIF_WM_HINTS";
        const string KDE_WM_ATOM = "KWM_WIN_DECORATION";
        const string KDE_NET_WM_ATOM = "_KDE_NET_WM_WINDOW_TYPE";
        const string ICCM_WM_ATOM = "_NET_WM_WINDOW_TYPE";
        const string ICON_NET_ATOM = "_NET_WM_ICON";

        // The Atom class from Mono might be useful to avoid calling XInternAtom by hand (somewhat error prone). 
        IntPtr _atom_wm_destroy;        
        
        IntPtr _atom_net_wm_state;
        IntPtr _atom_net_wm_state_minimized;
        IntPtr _atom_net_wm_state_fullscreen;
        IntPtr _atom_net_wm_state_maximized_horizontal;
        IntPtr _atom_net_wm_state_maximized_vertical;
        
        IntPtr _atom_net_wm_allowed_actions;
        IntPtr _atom_net_wm_action_resize;
        IntPtr _atom_net_wm_action_maximize_horizontally;
        IntPtr _atom_net_wm_action_maximize_vertically;

        IntPtr _atom_net_wm_icon;

        IntPtr _atom_net_frame_extents;

        readonly IntPtr _atom_xa_cardinal = new IntPtr(6);
        
        //IntPtr _atom_motif_wm_hints;
        //IntPtr _atom_kde_wm_hints;
        //IntPtr _atom_kde_net_wm_hints;
        
        static readonly IntPtr _atom_remove = (IntPtr)0;
        static readonly IntPtr _atom_add = (IntPtr)1;
        static readonly IntPtr _atom_toggle = (IntPtr)2;
        
        Rectangle bounds, client_rectangle;
        int border_left, border_right, border_top, border_bottom;
        Icon icon;
        bool has_focus;
        bool visible;

        // Used for event loop.
        XEvent e = new XEvent();

        bool disposed;
        bool exists;
        bool isExiting;

        bool _decorations_hidden = false;
        bool cursor_visible = true;
        int mouse_rel_x, mouse_rel_y;

         // Keyboard input
        readonly byte[] ascii = new byte[16];
        readonly char[] chars = new char[16];
        readonly KeyPressEventArgs KPEventArgs = new KeyPressEventArgs('\0');
        readonly KeyboardKeyEventArgs KeyDownEventArgs = new KeyboardKeyEventArgs();
        readonly KeyboardKeyEventArgs KeyUpEventArgs = new KeyboardKeyEventArgs();

        readonly IntPtr EmptyCursor;

        public static bool MouseWarpActive = false;

        #endregion

        #region Constructors

        public X11GLNative(int x, int y, int width, int height, string title,
            GraphicsMode mode,GameWindowFlags options, DisplayDevice device)
            : this()
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException("width", "Must be higher than zero.");
            if (height <= 0)
                throw new ArgumentOutOfRangeException("height", "Must be higher than zero.");

            XVisualInfo info = new XVisualInfo();

            Debug.Indent();
            
            using (new XLock(window.Display))
            {
                if (!mode.Index.HasValue)
                {
                    mode = new X11GraphicsMode().SelectGraphicsMode(
                        mode.ColorFormat, mode.Depth, mode.Stencil, mode.Samples,
                        mode.AccumulatorFormat, mode.Buffers, mode.Stereo);
                }

                info.VisualID = mode.Index.Value;
                int dummy;
                window.VisualInfo = (XVisualInfo)Marshal.PtrToStructure(
                    Functions.XGetVisualInfo(window.Display, XVisualInfoMask.ID, ref info, out dummy), typeof(XVisualInfo));

                // Create a window on this display using the visual above
                Debug.Write("Opening render window... ");

                XSetWindowAttributes attributes = new XSetWindowAttributes();
                attributes.background_pixel = IntPtr.Zero;
                attributes.border_pixel = IntPtr.Zero;
                attributes.colormap = Functions.XCreateColormap(window.Display, window.RootWindow, window.VisualInfo.Visual, 0/*AllocNone*/);
                window.EventMask = EventMask.StructureNotifyMask /*| EventMask.SubstructureNotifyMask*/ | EventMask.ExposureMask |
                                   EventMask.KeyReleaseMask | EventMask.KeyPressMask | EventMask.KeymapStateMask |
                                   EventMask.PointerMotionMask | EventMask.FocusChangeMask |
                                   EventMask.ButtonPressMask | EventMask.ButtonReleaseMask |
                                   EventMask.EnterWindowMask | EventMask.LeaveWindowMask |
                                   EventMask.PropertyChangeMask;
                attributes.event_mask = (IntPtr)window.EventMask;

                uint mask = (uint)SetWindowValuemask.ColorMap | (uint)SetWindowValuemask.EventMask |
                    (uint)SetWindowValuemask.BackPixel | (uint)SetWindowValuemask.BorderPixel;

                window.Handle = Functions.XCreateWindow(window.Display, window.RootWindow,
                    x, y, width, height, 0, window.VisualInfo.Depth/*(int)CreateWindowArgs.CopyFromParent*/,
                    (int)CreateWindowArgs.InputOutput, window.VisualInfo.Visual, (UIntPtr)mask, ref attributes);

                if (window.Handle == IntPtr.Zero)
                    throw new ApplicationException("XCreateWindow call failed (returned 0).");

                if (title != null)
                    Functions.XStoreName(window.Display, window.Handle, title);
            }

            // Set the window hints
            SetWindowMinMax(_min_width, _min_height, -1, -1);            
            
            XSizeHints hints = new XSizeHints();
            hints.base_width = width;
            hints.base_height = height;
            hints.flags = (IntPtr)(XSizeHintsFlags.PSize | XSizeHintsFlags.PPosition);
            using (new XLock(window.Display))
            {
                Functions.XSetWMNormalHints(window.Display, window.Handle, ref hints);

                // Register for window destroy notification
                Functions.XSetWMProtocols(window.Display, window.Handle, new IntPtr[] { _atom_wm_destroy }, 1);
            }

            // Set the initial window size to ensure X, Y, Width, Height and the rest
            // return the correct values inside the constructor and the Load event.
            XEvent e = new XEvent();
            e.ConfigureEvent.x = x;
            e.ConfigureEvent.y = y;
            e.ConfigureEvent.width = width;
            e.ConfigureEvent.height = height;
            RefreshWindowBounds(ref e);

            driver = new X11Input(window);
            keyboard = driver.Keyboard[0];
            mouse = driver.Mouse[0];

            EmptyCursor = CreateEmptyCursor(window);

            Debug.WriteLine(String.Format("X11GLNative window created successfully (id: {0}).", Handle));
            Debug.Unindent();

            exists = true;
        }

        /// <summary>
        /// Constructs and initializes a new X11GLNative window.
        /// Call CreateWindow to create the actual render window.
        /// </summary>
        public X11GLNative()
        {
            try
            {
                Debug.Print("Creating X11GLNative window.");
                Debug.Indent();

                // Open a display connection to the X server, and obtain the screen and root window.
                window.Display = Functions.XOpenDisplay(IntPtr.Zero);
                //window.Display = API.DefaultDisplay;
                if (window.Display == IntPtr.Zero)
                    throw new Exception("Could not open connection to X");

                using (new XLock(window.Display))
                {
                    window.Screen = Functions.XDefaultScreen(window.Display); //API.DefaultScreen;
                    window.RootWindow = Functions.XRootWindow(window.Display, window.Screen); // API.RootWindow;
                }

                Debug.Print("Display: {0}, Screen {1}, Root window: {2}", window.Display, window.Screen,
                            window.RootWindow);
                
                RegisterAtoms(window);
            }
            finally
            {
                Debug.Unindent();
            }
        }

        #endregion

        #region Private Members

        #region private void RegisterAtoms()

        /// <summary>
        /// Not used yet.
        /// Registers the necessary atoms for GameWindow.
        /// </summary>
        private void RegisterAtoms(X11WindowInfo window)
        {
            using (new XLock(window.Display))
            {
                Debug.WriteLine("Registering atoms.");
                _atom_wm_destroy = Functions.XInternAtom(window.Display, "WM_DELETE_WINDOW", true);
            
                _atom_net_wm_state = Functions.XInternAtom(window.Display, "_NET_WM_STATE", false);
                _atom_net_wm_state_minimized = Functions.XInternAtom(window.Display, "_NET_WM_STATE_MINIMIZED", false);
                _atom_net_wm_state_fullscreen = Functions.XInternAtom(window.Display, "_NET_WM_STATE_FULLSCREEN", false);
                _atom_net_wm_state_maximized_horizontal =
                    Functions.XInternAtom(window.Display, "_NET_WM_STATE_MAXIMIZED_HORZ", false);
                _atom_net_wm_state_maximized_vertical =
                    Functions.XInternAtom(window.Display, "_NET_WM_STATE_MAXIMIZED_VERT", false);
            
                _atom_net_wm_allowed_actions =
                    Functions.XInternAtom(window.Display, "_NET_WM_ALLOWED_ACTIONS", false);
                _atom_net_wm_action_resize =
                    Functions.XInternAtom(window.Display, "_NET_WM_ACTION_RESIZE", false);
                _atom_net_wm_action_maximize_horizontally =
                    Functions.XInternAtom(window.Display, "_NET_WM_ACTION_MAXIMIZE_HORZ", false);
                _atom_net_wm_action_maximize_vertically =
                    Functions.XInternAtom(window.Display, "_NET_WM_ACTION_MAXIMIZE_VERT", false);

                _atom_net_wm_icon =
                    Functions.XInternAtom(window.Display, "_NEW_WM_ICON", false);

                _atom_net_frame_extents =
                    Functions.XInternAtom(window.Display, "_NET_FRAME_EXTENTS", false);
            
//            string[] atom_names = new string[]
//            {
//                //"WM_TITLE",
//                //"UTF8_STRING"
//            };
//            IntPtr[] atoms = new IntPtr[atom_names.Length];
//            //Functions.XInternAtoms(window.Display, atom_names, atom_names.Length, false, atoms);
//
//            int offset = 0;
//            //WMTitle = atoms[offset++];
//            //UTF8String = atoms[offset++];
            }
        }

        #endregion

        #region SetWindowMinMax
        
        void SetWindowMinMax(int min_width, int min_height, int max_width, int max_height)
        {
            SetWindowMinMax((short)min_width, (short)min_height, (short)max_width, (short)max_height);
        }

        void SetWindowMinMax(short min_width, short min_height, short max_width, short max_height)
        {
            IntPtr dummy;
            XSizeHints hints = new XSizeHints();

            using (new XLock(window.Display))
            {
                Functions.XGetWMNormalHints(window.Display, window.Handle, ref hints, out dummy);
            }

            if (min_width > 0 || min_height > 0)
            {
                hints.flags = (IntPtr)((int)hints.flags | (int)XSizeHintsFlags.PMinSize);
                hints.min_width = min_width;
                hints.min_height = min_height;
            }
            else
                hints.flags = (IntPtr)((int)hints.flags & ~(int)XSizeHintsFlags.PMinSize);

            if (max_width > 0 || max_height > 0)
            {
                hints.flags = (IntPtr)((int)hints.flags | (int)XSizeHintsFlags.PMaxSize);
                hints.max_width = max_width;
                hints.max_height = max_height;
            }
            else
                hints.flags = (IntPtr)((int)hints.flags & ~(int)XSizeHintsFlags.PMaxSize);

            if (hints.flags != IntPtr.Zero)
            {
                // The Metacity team has decided that they won't care about this when clicking the maximize
                // icon, will maximize the window to fill the screen/parent no matter what.
                // http://bugzilla.ximian.com/show_bug.cgi?id=80021
                using (new XLock(window.Display))
                {
                    Functions.XSetWMNormalHints(window.Display, window.Handle, ref hints);
                }
            }
        }

        #endregion

        #region IsWindowBorderResizable

        bool IsWindowBorderResizable
        {
            get
            {
                IntPtr actual_atom;
                int actual_format;
                IntPtr nitems;
                IntPtr bytes_after;
                IntPtr prop = IntPtr.Zero;
                IntPtr atom;
                //XWindowAttributes attributes;                

                using (new XLock(window.Display))
                {
                    Functions.XGetWindowProperty(window.Display, window.Handle,
                                                 _atom_net_wm_allowed_actions, IntPtr.Zero, new IntPtr(256), false,
                                                 IntPtr.Zero, out actual_atom, out actual_format, out nitems,
                                                 out bytes_after, ref prop);
                    if ((long)nitems > 0 && prop != IntPtr.Zero)
                    {
                        for (int i = 0; i < (long)nitems; i++)
                        {
                            atom = (IntPtr)Marshal.ReadIntPtr(prop, i * IntPtr.Size);
    
                            if (atom == _atom_net_wm_action_resize)
                                return true;
                        }
                        Functions.XFree(prop);
                    }
                }
                    
                return false;
            }
        }

        #endregion
                
        #region bool IsWindowBorderHidden
                
        bool IsWindowBorderHidden
        {
            get
            {                
                //IntPtr actual_atom;
                //int actual_format;
                //IntPtr nitems;
                //IntPtr bytes_after;
                IntPtr prop = IntPtr.Zero;
                //IntPtr atom;
                //XWindowAttributes attributes;

                using (new XLock(window.Display))
                {
                    // Test if decorations have been disabled through Motif.
                    IntPtr motif_hints_atom = Functions.XInternAtom(this.window.Display, MOTIF_WM_ATOM, true);
                    if (motif_hints_atom != IntPtr.Zero)
                    {
                        // TODO: How to check if MotifWMHints decorations have been really disabled?
                        if (_decorations_hidden)
                            return true;
                    }
    
                    // Some WMs remove decorations when the transient_for hint is set. Most new ones do not (but those
                    // should obey the Motif hint). Anyway, if this hint is set, we say the decorations have been remove
                    // although there is a slight chance this is not the case.
                    IntPtr transient_for_parent;
                    Functions.XGetTransientForHint(window.Display, window.Handle, out transient_for_parent);
                    if (transient_for_parent != IntPtr.Zero)
                        return true;
    
                    return false;
                }
            }
        }
                
        #endregion

        #region void DisableWindowDecorations()

        void DisableWindowDecorations()
        {
            if (DisableMotifDecorations())
            {
                Debug.Print("Removed decorations through motif.");
                _decorations_hidden = true;
            }
            
            using (new XLock(window.Display))
            {
                // Functions.XSetTransientForHint(this.window.Display, this.Handle, this.window.RootWindow);

                // Some WMs remove decorations when this hint is set. Doesn't hurt to try.
                Functions.XSetTransientForHint(this.window.Display, this.Handle, this.window.RootWindow);

                if (_decorations_hidden)
                {
                    Functions.XUnmapWindow(this.window.Display, this.Handle);
                    Functions.XMapWindow(this.window.Display, this.Handle);
                }
            }
        }
        

        #region bool DisableMotifDecorations()

        bool DisableMotifDecorations()
        {
            using (new XLock(window.Display))
            {
                IntPtr atom = Functions.XInternAtom(this.window.Display, MOTIF_WM_ATOM, true);
                if (atom != IntPtr.Zero)
                {
                    //Functions.XGetWindowProperty(window.Display, window.Handle, atom, IntPtr.Zero, IntPtr.Zero, false,
                                                 
                    MotifWmHints hints = new MotifWmHints();
                    hints.flags = (IntPtr)MotifFlags.Decorations;
                    Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace,
                                              ref hints, Marshal.SizeOf(hints) / IntPtr.Size);
                    return true;
                }
                return false;
            }
        }

        #endregion

        #region bool DisableGnomeDecorations()

        bool DisableGnomeDecorations()
        {
            using (new XLock(window.Display))
            {
                IntPtr atom = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_HINTS, true);
                if (atom != IntPtr.Zero)
                {
                    IntPtr hints = IntPtr.Zero;
                    Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace,
                                              ref hints, Marshal.SizeOf(hints) / IntPtr.Size);
                    return true;
                }
    
                return false;
            }
        }

        #endregion

        #endregion

        #region void EnableWindowDecorations()

        void EnableWindowDecorations()
        {
            if (EnableMotifDecorations())
            {
                Debug.Print("Activated decorations through motif.");
                _decorations_hidden = false;
            }

            //if (EnableGnomeDecorations()) { Debug.Print("Activated decorations through gnome."); activated = true; }

            using (new XLock(window.Display))
            {
                Functions.XSetTransientForHint(this.window.Display, this.Handle, IntPtr.Zero);

                if (!_decorations_hidden)
                {
                    Functions.XUnmapWindow(this.window.Display, this.Handle);
                    Functions.XMapWindow(this.window.Display, this.Handle);
                }
            }
        }

        #region bool EnableMotifDecorations()

        bool EnableMotifDecorations()
        {
            using (new XLock(window.Display))
            {
                IntPtr atom = Functions.XInternAtom(this.window.Display, MOTIF_WM_ATOM, true);
                if (atom != IntPtr.Zero)
                {
                    //Functions.XDeleteProperty(this.window.Display, this.Handle, atom);
                    MotifWmHints hints = new MotifWmHints();
                    hints.flags = (IntPtr)MotifFlags.Decorations;
                    hints.decorations = (IntPtr)MotifDecorations.All;
                    Functions.XChangeProperty(this.window.Display, this.Handle, atom, atom, 32, PropertyMode.Replace,
                                              ref hints, Marshal.SizeOf(hints) / IntPtr.Size);
    
                    return true;
                }
                return false;
            }
        }

        #endregion

        #region bool EnableGnomeDecorations()

        bool EnableGnomeDecorations()
        {
            using (new XLock(window.Display))
            {
                // Restore window layer.
                //XEvent xev = new XEvent();
                //xev.ClientMessageEvent.window = this.window.Handle;
                //xev.ClientMessageEvent.type = XEventName.ClientMessage;
                //xev.ClientMessageEvent.message_type = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_LAYER, false);
                //xev.ClientMessageEvent.format = 32;
                //xev.ClientMessageEvent.ptr1 = (IntPtr)WindowLayer.AboveDock;
                //Functions.XSendEvent(this.window.Display, this.window.RootWindow, false, (IntPtr)EventMask.SubstructureNotifyMask, ref xev);
    
                IntPtr atom = Functions.XInternAtom(this.window.Display, Constants.XA_WIN_HINTS, true);
                if (atom != IntPtr.Zero)
                {
                    Functions.XDeleteProperty(this.window.Display, this.Handle, atom);
                    return true;
                }
    
                return false;
            }
        }

        #endregion

        #endregion

        #region DeleteIconPixmaps
        
        static void DeleteIconPixmaps(IntPtr display, IntPtr window)
        {
            using (new XLock(display))
            {
                IntPtr wmHints_ptr = Functions.XGetWMHints(display, window);
    
                if (wmHints_ptr != IntPtr.Zero)
                {
                    XWMHints wmHints = (XWMHints)Marshal.PtrToStructure(wmHints_ptr, typeof(XWMHints));
                    XWMHintsFlags flags = (XWMHintsFlags)wmHints.flags.ToInt32();
    
                    if ((flags & XWMHintsFlags.IconPixmapHint) != 0)
                    {
                        wmHints.flags = new IntPtr((int)(flags & ~XWMHintsFlags.IconPixmapHint));
                        Functions.XFreePixmap(display, wmHints.icon_pixmap);
                    }
    
                    if ((flags & XWMHintsFlags.IconMaskHint) != 0)
                    {
                        wmHints.flags = new IntPtr((int)(flags & ~XWMHintsFlags.IconMaskHint));
                        Functions.XFreePixmap(display, wmHints.icon_mask);
                    }
    
                    Functions.XSetWMHints(display, window, ref wmHints);
                    Functions.XFree(wmHints_ptr);
                }
            }
        }

        #endregion

        bool RefreshWindowBorders()
        {
            bool borders_changed = false;

            if (IsWindowBorderHidden)
            {
                borders_changed =
                    border_left != 0 ||
                    border_right != 0 ||
                    border_top != 0 ||
                    border_bottom != 0;
                
                border_left = 0;
                border_right = 0;
                border_top = 0;
                border_bottom = 0;
            }
            else
            {
                IntPtr atom, nitems, bytes_after, prop = IntPtr.Zero;
                int format;
    
                using (new XLock(window.Display))
                {
                    Functions.XGetWindowProperty(window.Display, window.Handle,
                        _atom_net_frame_extents, IntPtr.Zero, new IntPtr(16), false,
                        (IntPtr)Atom.XA_CARDINAL, out atom, out format, out nitems, out bytes_after, ref prop);
                }
    
                if ((prop != IntPtr.Zero))
                {
                    if ((long)nitems == 4)
                    {
                        int new_border_left = Marshal.ReadIntPtr(prop, 0).ToInt32();
                        int new_border_right = Marshal.ReadIntPtr(prop, IntPtr.Size).ToInt32();
                        int new_border_top = Marshal.ReadIntPtr(prop, IntPtr.Size * 2).ToInt32();
                        int new_border_bottom = Marshal.ReadIntPtr(prop, IntPtr.Size * 3).ToInt32();
    
                        borders_changed =
                            new_border_left != border_left ||
                            new_border_right != border_right ||
                            new_border_top != border_top ||
                            new_border_bottom != border_bottom;
    
                        border_left = new_border_left;
                        border_right = new_border_right;
                        border_top = new_border_top;
                        border_bottom = new_border_bottom;
    
                        //Debug.WriteLine(border_left);
                        //Debug.WriteLine(border_right);
                        //Debug.WriteLine(border_top);
                        //Debug.WriteLine(border_bottom);
                    }
    
                    using (new XLock(window.Display))
                    {
                        Functions.XFree(prop);
                    }
                }
            }

            return borders_changed;
        }

        void RefreshWindowBounds(ref XEvent e)
        {
            RefreshWindowBorders();

            // For whatever reason, the x/y coordinates
            // of a configure event are global to the
            // root window when it is a send_event but
            // local when it is a regular event.
            // I don't know who designed this, but this is
            // utter nonsense.
            int x, y;
            IntPtr unused;
            if (!e.ConfigureEvent.send_event)
            {
                Functions.XTranslateCoordinates(window.Display,
                    window.Handle, window.RootWindow,
                    0, 0, out x, out y, out unused);
            }
            else
            {
                x = e.ConfigureEvent.x;
                y = e.ConfigureEvent.y;
            }
            
            Point new_location = new Point(
                x - border_left,
                y - border_top);

            if (Location != new_location)
            {
                bounds.Location = new_location;
                Move(this, EventArgs.Empty);
            }

            // Note: width and height denote the internal (client) size.
            // To get the external (window) size, we need to add the border size.
            Size new_size = new Size(
                e.ConfigureEvent.width + border_left + border_right,
                e.ConfigureEvent.height + border_top + border_bottom);
            if (Bounds.Size != new_size)
            {
                bounds.Size = new_size;
                client_rectangle.Size = new Size(e.ConfigureEvent.width, e.ConfigureEvent.height);

                Resize(this, EventArgs.Empty);
            }

            //Debug.Print("[X11] Window bounds changed: {0}", bounds);
        }

        static IntPtr CreateEmptyCursor(X11WindowInfo window)
        {
            IntPtr cursor = IntPtr.Zero;
            using (new XLock(window.Display))
            {
                XColor black, dummy;
                IntPtr cmap = Functions.XDefaultColormap(window.Display, window.Screen);
                Functions.XAllocNamedColor(window.Display, cmap, "black", out black, out dummy);
                IntPtr bmp_empty = Functions.XCreateBitmapFromData(window.Display,
                    window.Handle, new byte[,] { { 0 } });
                cursor = Functions.XCreatePixmapCursor(window.Display,
                    bmp_empty, bmp_empty, ref black, ref black, 0, 0);
            }
            return cursor;
        }

        static void SetMouseClamped(MouseDevice mouse, int x, int y,
            int left, int top, int width, int height)
        {
            // Clamp mouse to the specified rectangle.
            x = Math.Max(x, left);
            x = Math.Min(x, width);
            y = Math.Max(y, top);
            y = Math.Min(y, height);
            mouse.Position = new Point(x, y);
        }

        #endregion

        #region INativeWindow Members

        #region ProcessEvents

        public void ProcessEvents()
        {
            // Process all pending events
            while (Exists && window != null)
            {
                using (new XLock(window.Display))
                {
                    if (!Functions.XCheckWindowEvent(window.Display, window.Handle, window.EventMask, ref e) &&
                        !Functions.XCheckTypedWindowEvent(window.Display, window.Handle, XEventName.ClientMessage, ref e))
                        break;
                }
                
                // Respond to the event e
                switch (e.type)
                {
                    case XEventName.MapNotify:
                        {
                            bool previous_visible = visible;
                            visible = true;
                            if (visible != previous_visible)
                                VisibleChanged(this, EventArgs.Empty);
                        }
                        return;

                    case XEventName.UnmapNotify:
                        {
                            bool previous_visible = visible;
                            visible = false;
                            if (visible != previous_visible)
                                VisibleChanged(this, EventArgs.Empty);
                        }
                        break;

                    case XEventName.CreateNotify:
                        // A child was was created - nothing to do
                        break;

                    case XEventName.ClientMessage:
                        if (!isExiting && e.ClientMessageEvent.ptr1 == _atom_wm_destroy)
                        {
                            Debug.WriteLine("Exit message received.");
                            CancelEventArgs ce = new CancelEventArgs();
                            Closing(this, ce);

                            if (!ce.Cancel)
                            {
                                isExiting = true;
                                
                                Debug.WriteLine("Destroying window.");
                                using (new XLock(window.Display))
                                {
                                    Functions.XDestroyWindow(window.Display, window.Handle);
                                }
                                break;
                            }
                        }
                        
                        break;

                    case XEventName.DestroyNotify:
                        Debug.WriteLine("Window destroyed");
                        exists = false;

                        Closed(this, EventArgs.Empty);

                        return;

                    case XEventName.ConfigureNotify:
                        RefreshWindowBounds(ref e);
                        break;

                    case XEventName.KeyPress:
                    case XEventName.KeyRelease:
                        bool pressed = e.type == XEventName.KeyPress;
                        Key key;
                        if (driver.TranslateKey(ref e.KeyEvent, out key))
                        {
                            // Update legacy GameWindow.Keyboard API:
                            keyboard.SetKey(key, (uint)e.KeyEvent.keycode, pressed);

                            if (pressed)
                            {
                                // Raise KeyDown event
                                KeyDownEventArgs.Key = key;
                                KeyDownEventArgs.ScanCode = (uint)e.KeyEvent.keycode;
                                KeyDownEventArgs.Modifiers = keyboard.GetModifiers();
                                KeyDown(this, KeyDownEventArgs);
                            }
                            else
                            {
                                // Raise KeyUp event
                                KeyUpEventArgs.Key = key;
                                KeyUpEventArgs.ScanCode = (uint)e.KeyEvent.keycode;
                                KeyUpEventArgs.Modifiers = keyboard.GetModifiers();
                                KeyUp(this, KeyUpEventArgs);
                            }

                            if (pressed)
                            {
                                // Translate XKeyPress to characters and
                                // raise KeyPress events
                                int status = 0;
                                status = Functions.XLookupString(
                                    ref e.KeyEvent, ascii, ascii.Length, null, IntPtr.Zero);
                                Encoding.Default.GetChars(ascii, 0, status, chars, 0);
    
                                for (int i = 0; i < status; i++)
                                {
                                    if (!Char.IsControl(chars[i]))
                                    {
                                        KPEventArgs.KeyChar = chars[i];
                                        KeyPress(this, KPEventArgs);
                                    }
                                }
                            }
                        }
                        break;

                    case XEventName.MotionNotify:
                    {
                        // Try to detect and ignore events from XWarpPointer, below.
                        // This heuristic will fail if the user actually moves the pointer
                        // to the dead center of the window. Fortunately, this situation
                        // is very very uncommon. Todo: Can this be remedied?
                        int x = e.MotionEvent.x;
                        int y =e.MotionEvent.y;
                        // TODO: Have offset as a stored field, only update it when the window moves
                        // The middle point cannot be the average of the Bounds.left/right/top/bottom,
                        // because these fields take into account window decoration (borders, etc),
                        // which we do not want to account for.
                        Point offset = this.PointToClient(Point.Empty);
                        int middle_x = Width/2-offset.X;
                        int middle_y = Height/2-offset.Y;

                        Point screen_xy = PointToScreen(new Point(x, y));
                        if (!CursorVisible && MouseWarpActive &&
                            screen_xy.X == middle_x && screen_xy.Y == middle_y)
                        {
                            MouseWarpActive = false;
                            mouse_rel_x = x;
                            mouse_rel_y = y;
                        }
                        else if (!CursorVisible)
                        {
                            SetMouseClamped(mouse,
                                mouse.X + x - mouse_rel_x,
                                mouse.Y + y - mouse_rel_y,
                                0, 0, Width, Height);
                            mouse_rel_x = x;
                            mouse_rel_y = y;

                            // Warp cursor to center of window.
                            MouseWarpActive = true;
                            Mouse.SetPosition(middle_x, middle_y);
                        }
                        else
                        {
                            SetMouseClamped(mouse, x, y, 0, 0, Width, Height);
                            mouse_rel_x = x;
                            mouse_rel_y = y;
                        }
                        break;
                    }

                    case XEventName.ButtonPress:
                    case XEventName.ButtonRelease:
                        driver.ProcessEvent(ref e);
                        break;

                    case XEventName.FocusIn:
                        {
                            bool previous_focus = has_focus;
                            has_focus = true;
                            if (has_focus != previous_focus)
                                FocusedChanged(this, EventArgs.Empty);
                        }
                        break;

                    case XEventName.FocusOut:
                        {
                            bool previous_focus = has_focus;
                            has_focus = false;
                            if (has_focus != previous_focus)
                                FocusedChanged(this, EventArgs.Empty);
                        }
                        break;

                    case XEventName.LeaveNotify:
                        if (CursorVisible)
                        {
                            MouseLeave(this, EventArgs.Empty);
                        }
                        break;

                    case XEventName.EnterNotify:
                        MouseEnter(this, EventArgs.Empty);
                        break;

                    case XEventName.MappingNotify:
                        // 0 == MappingModifier, 1 == MappingKeyboard
                        if (e.MappingEvent.request == 0 || e.MappingEvent.request == 1)
                        {
                            Debug.Print("keybard mapping refreshed");
                            Functions.XRefreshKeyboardMapping(ref e.MappingEvent);
                        }
                        break;

                   case XEventName.PropertyNotify:
                        if (e.PropertyEvent.atom == _atom_net_wm_state)
                        {
                            WindowStateChanged(this, EventArgs.Empty);
                        }

                        //if (e.PropertyEvent.atom == _atom_net_frame_extents)
                        //{
                        //    RefreshWindowBorders();
                        //}
                        break;
                       
                    default:
                        //Debug.WriteLine(String.Format("{0} event was not handled", e.type));
                        break;
                }
            }
        }

        #endregion

        #region Bounds

        public Rectangle Bounds
        {
            get
            {
                return bounds;
            }
            set
            {
                bool is_location_changed = bounds.Location != value.Location;
                bool is_size_changed = bounds.Size != value.Size;

                int x = value.X;
                int y = value.Y;
                int width = value.Width - border_left - border_right;
                int height = value.Height - border_top - border_bottom;

                if (WindowBorder != WindowBorder.Resizable)
                {
                    SetWindowMinMax(width, height, width, height);
                }

                using (new XLock(window.Display))
                {
                    if (is_location_changed && is_size_changed)
                    {
                        Functions.XMoveResizeWindow(window.Display, window.Handle,
                            x, y, width, height);
                    }
                    else if (is_location_changed)
                    {
                        Functions.XMoveWindow(window.Display, window.Handle,
                            x, y);
                    }
                    else if (is_size_changed)
                    {
                        Functions.XResizeWindow(window.Display, window.Handle,
                            width, height);
                    }
                }

                ProcessEvents();
            }
        }

        #endregion

        #region Location

        public Point Location
        {
            get { return Bounds.Location; }
            set
            {
                Bounds = new Rectangle(value, Bounds.Size);
            }
        }

        #endregion

        #region Size

        public Size Size
        {
            get { return Bounds.Size; }
            set
            {
                Bounds = new Rectangle(Bounds.Location, value);
            }
        }

        #endregion

        #region ClientRectangle

        public Rectangle ClientRectangle
        {
            get
            {
                if (client_rectangle.Width == 0)
                    client_rectangle.Width = 1;
                if (client_rectangle.Height == 0)
                    client_rectangle.Height = 1;
                return client_rectangle;
            }
            set
            {
                using (new XLock(window.Display))
                {
                    Functions.XMoveWindow(window.Display, window.Handle,
                        value.X, value.Y);
                    Functions.XResizeWindow(window.Display, window.Handle,
                        value.Width, value.Height);
                }
                ProcessEvents();
            }
        }

        #endregion

        #region ClientSize

        public Size ClientSize
        {
            get
            {
                return ClientRectangle.Size;
            }
            set
            {
                ClientRectangle = new Rectangle(Point.Empty, value);
            }
        }

        #endregion

        #region Width

        public int Width
        {
            get { return ClientSize.Width; }
            set { ClientSize = new Size(value, Height); }
        }

        #endregion

        #region Height

        public int Height
        {
            get { return ClientSize.Height; }
            set { ClientSize = new Size(Width, value); }
        }

        #endregion

        #region X

        public int X
        {
            get { return Location.X; }
            set { Location = new Point(value, Y); }
        }

        #endregion

        #region Y

        public int Y
        {
            get { return Location.Y; }
            set { Location = new Point(X, value); }
        }

        #endregion

        #region Icon

        public Icon Icon
        {
            get
            {
                return icon;
            }
            set
            {
                if (value == icon)
                    return;

                // Note: it seems that Gnome/Metacity does not respect the _NET_WM_ICON hint.
                // For this reason, we'll also set the icon using XSetWMHints.
                if (value == null)
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XDeleteProperty(window.Display, window.Handle, _atom_net_wm_icon);
                        DeleteIconPixmaps(window.Display, window.Handle);
                    }
                }
                else
                {
                    // Set _NET_WM_ICON
                    Bitmap bitmap = value.ToBitmap();
                    int size = bitmap.Width * bitmap.Height + 2;
                    IntPtr[] data = new IntPtr[size];
                    int index = 0;
    
                    data[index++] = (IntPtr)bitmap.Width;
                    data[index++] = (IntPtr)bitmap.Height;
    
                    for (int y = 0; y < bitmap.Height; y++)
                        for (int x = 0; x < bitmap.Width; x++)
                            data[index++] = (IntPtr)bitmap.GetPixel(x, y).ToArgb();

                    using (new XLock(window.Display))
                    {
                        Functions.XChangeProperty(window.Display, window.Handle,
                                      _atom_net_wm_icon, _atom_xa_cardinal, 32,
                                      PropertyMode.Replace, data, size);
                    }

                    // Set XWMHints
                    DeleteIconPixmaps(window.Display, window.Handle);
                    using (new XLock(window.Display))
                    {
                        IntPtr wmHints_ptr = Functions.XGetWMHints(window.Display, window.Handle);
    
                        if (wmHints_ptr == IntPtr.Zero)
                            wmHints_ptr = Functions.XAllocWMHints();
    
                        XWMHints wmHints = (XWMHints)Marshal.PtrToStructure(wmHints_ptr, typeof(XWMHints));
    
                        wmHints.flags = new IntPtr(wmHints.flags.ToInt32() | (int)(XWMHintsFlags.IconPixmapHint | XWMHintsFlags.IconMaskHint));
                        wmHints.icon_pixmap = Functions.CreatePixmapFromImage(window.Display, bitmap);
                        wmHints.icon_mask = Functions.CreateMaskFromImage(window.Display, bitmap);
    
                        Functions.XSetWMHints(window.Display, window.Handle, ref wmHints);
                        Functions.XFree (wmHints_ptr);
    
                        Functions.XSync(window.Display, false);
                    }
                }

                icon = value;
                IconChanged(this, EventArgs.Empty);
            }
        }

        #endregion

        #region Focused

        public bool Focused
        {
            get
            {
                return has_focus;
            }
        }

        #endregion

        #region WindowState

        public OpenTK.WindowState WindowState
        {
            get
            {
                IntPtr actual_atom;
                int actual_format;
                IntPtr nitems;
                IntPtr bytes_after;
                IntPtr prop = IntPtr.Zero;
                IntPtr atom;
                //XWindowAttributes attributes;
                bool fullscreen = false;
                int maximized = 0;
                bool minimized = false;

                using (new XLock(window.Display))
                {
                    Functions.XGetWindowProperty(window.Display, window.Handle,
                                 _atom_net_wm_state, IntPtr.Zero, new IntPtr(256), false,
                                 new IntPtr(4) /*XA_ATOM*/, out actual_atom, out actual_format,
                                 out nitems, out bytes_after, ref prop);
                }

                if ((long)nitems > 0 && prop != IntPtr.Zero)
                {
                    for (int i = 0; i < (long)nitems; i++)
                    {
                        atom = (IntPtr)Marshal.ReadIntPtr(prop, i * IntPtr.Size);

                        if (atom == _atom_net_wm_state_maximized_horizontal ||
                            atom == _atom_net_wm_state_maximized_vertical)
                            maximized++;
                        else if (atom == _atom_net_wm_state_minimized)
                            minimized = true;
                        else if (atom == _atom_net_wm_state_fullscreen)
                            fullscreen = true;
                    }
                    using (new XLock(window.Display))
                    {
                        Functions.XFree(prop);
                    }
                }

                if (minimized)
                    return OpenTK.WindowState.Minimized;
                else if (maximized == 2)
                    return OpenTK.WindowState.Maximized;
                else if (fullscreen)
                    return OpenTK.WindowState.Fullscreen;
                /*
                                attributes = new XWindowAttributes();
                                Functions.XGetWindowAttributes(window.Display, window.Handle, ref attributes);
                                if (attributes.map_state == MapState.IsUnmapped)
                                    return (OpenTK.WindowState)(-1);
                */
                return OpenTK.WindowState.Normal;
            }
            set
            {
                OpenTK.WindowState current_state = this.WindowState;

                if (current_state == value)
                    return;

                Debug.Print("GameWindow {0} changing WindowState from {1} to {2}.", window.Handle.ToString(),
                            current_state.ToString(), value.ToString());

                using (new XLock(window.Display))
                {
                    // Reset the current window state
                    if (current_state == OpenTK.WindowState.Minimized)
                        Functions.XMapWindow(window.Display, window.Handle);
                    else if (current_state == OpenTK.WindowState.Fullscreen)
                        Functions.SendNetWMMessage(window, _atom_net_wm_state, _atom_remove,
                                                  _atom_net_wm_state_fullscreen,
                                                   IntPtr.Zero);
                    else if (current_state == OpenTK.WindowState.Maximized)
                        Functions.SendNetWMMessage(window, _atom_net_wm_state, _atom_toggle,
                                                  _atom_net_wm_state_maximized_horizontal,
                                                  _atom_net_wm_state_maximized_vertical);
    
                    Functions.XSync(window.Display, false);
                }
                // We can't resize the window if its border is fixed, so make it resizable first.
                bool temporary_resizable = false;
                WindowBorder previous_state = WindowBorder;
                if (WindowBorder != WindowBorder.Resizable)
                {
                    temporary_resizable = true;
                    WindowBorder = WindowBorder.Resizable;
                }

                using (new XLock(window.Display))
                {
                    switch (value)
                    {
                        case OpenTK.WindowState.Normal:
                            Functions.XRaiseWindow(window.Display, window.Handle);
    
                            break;
    
                        case OpenTK.WindowState.Maximized:
                            Functions.SendNetWMMessage(window, _atom_net_wm_state, _atom_add,
                                                      _atom_net_wm_state_maximized_horizontal,
                                                      _atom_net_wm_state_maximized_vertical);
                            Functions.XRaiseWindow(window.Display, window.Handle);
    
                            break;
    
                        case OpenTK.WindowState.Minimized:
                            // Todo: multiscreen support
                            Functions.XIconifyWindow(window.Display, window.Handle, window.Screen);
    
                            break;
    
                        case OpenTK.WindowState.Fullscreen:
                            //_previous_window_border = this.WindowBorder;
                            //this.WindowBorder = WindowBorder.Hidden;
                            Functions.SendNetWMMessage(window, _atom_net_wm_state, _atom_add,
                                                      _atom_net_wm_state_fullscreen, IntPtr.Zero);
                            Functions.XRaiseWindow(window.Display, window.Handle);
    
                            break;
                    }
                }

                if (temporary_resizable)
                    WindowBorder = previous_state;

                ProcessEvents();
            }
        }

        #endregion

        #region WindowBorder

        public OpenTK.WindowBorder WindowBorder
        {
            get
            {
                if (IsWindowBorderHidden)
                    return WindowBorder.Hidden;

                if (IsWindowBorderResizable)
                    return WindowBorder.Resizable;
                else
                    return WindowBorder.Fixed;
            }
            set
            {
                if (WindowBorder == value)
                    return;

                if (WindowBorder == WindowBorder.Hidden)
                    EnableWindowDecorations();

                switch (value)
                {
                    case WindowBorder.Fixed:
                        Debug.Print("Making WindowBorder fixed.");
                        SetWindowMinMax((short)Width, (short)Height, (short)Width, (short)Height);

                        break;

                    case WindowBorder.Resizable:
                        Debug.Print("Making WindowBorder resizable.");
                        SetWindowMinMax(_min_width, _min_height, -1, -1);

                        break;

                    case WindowBorder.Hidden:
                        Debug.Print("Making WindowBorder hidden.");
                        DisableWindowDecorations();

                        break;
                }

                WindowBorderChanged(this, EventArgs.Empty);
            }
        }

        #endregion

        #region Events

        public event EventHandler<EventArgs> Move = delegate { };
        public event EventHandler<EventArgs> Resize = delegate { };
        public event EventHandler<System.ComponentModel.CancelEventArgs> Closing = delegate { };
        public event EventHandler<EventArgs> Closed = delegate { };
        public event EventHandler<EventArgs> Disposed = delegate { };
        public event EventHandler<EventArgs> IconChanged = delegate { };
        public event EventHandler<EventArgs> TitleChanged = delegate { };
        public event EventHandler<EventArgs> VisibleChanged = delegate { };
        public event EventHandler<EventArgs> FocusedChanged = delegate { };
        public event EventHandler<EventArgs> WindowBorderChanged = delegate { };
        public event EventHandler<EventArgs> WindowStateChanged = delegate { };
        public event EventHandler<KeyboardKeyEventArgs> KeyDown = delegate { };
        public event EventHandler<KeyPressEventArgs> KeyPress = delegate { };
        public event EventHandler<KeyboardKeyEventArgs> KeyUp = delegate { };
        public event EventHandler<EventArgs> MouseEnter = delegate { };
        public event EventHandler<EventArgs> MouseLeave = delegate { };
        
        #endregion

        public bool CursorVisible
        {
            get { return cursor_visible; }
            set
            {
                if (value)
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XUndefineCursor(window.Display, window.Handle);
                        cursor_visible = true;
                    }
                }
                else
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XDefineCursor(window.Display, window.Handle, EmptyCursor);
                        cursor_visible = false;
                    }
                }
            }
        }

        #endregion

        #region --- INativeGLWindow Members ---

        #region public IInputDriver InputDriver

        public IInputDriver InputDriver
        {
            get
            {
                return driver;
            }
        }

        #endregion 

        #region public bool Exists

        /// <summary>
        /// Returns true if a render window/context exists.
        /// </summary>
        public bool Exists
        {
            get { return exists; }
        }

        #endregion

        #region public bool IsIdle

        public bool IsIdle
        {
            get { throw new Exception("The method or operation is not implemented."); }
        }

        #endregion

        #region public IntPtr Handle

        /// <summary>
        /// Gets the current window handle.
        /// </summary>
        public IntPtr Handle
        {
            get { return this.window.Handle; }
        }

        #endregion

        #region public string Title

        /// <summary>
        /// TODO: Use atoms for this property.
        /// Gets or sets the GameWindow title.
        /// </summary>
        public string Title
        {
            get
            {
                IntPtr name = IntPtr.Zero;
                using (new XLock(window.Display))
                {
                    Functions.XFetchName(window.Display, window.Handle, ref name);
                }
                if (name != IntPtr.Zero)
                    return Marshal.PtrToStringAnsi(name);

                return String.Empty;
            }
            set
            {
                if (value != null && value != Title)
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XStoreName(window.Display, window.Handle, value);
                    }
                }

                TitleChanged(this, EventArgs.Empty);
            }
        }

        #endregion

        #region public bool Visible

        public bool Visible
        {
            get
            {
                return visible;
            }
            set
            {
                if (value && !visible)
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XMapWindow(window.Display, window.Handle);
                    }
                }
                else if (!value && visible)
                {
                    using (new XLock(window.Display))
                    {
                        Functions.XUnmapWindow(window.Display, window.Handle);
                    }
                }
            }
        }

        #endregion

        #region public IWindowInfo WindowInfo

        public IWindowInfo WindowInfo
        {
            get { return window; }
        }

        #endregion

        public void Close() { Exit(); }

        #region public void Exit()

        public void Exit()
        {
            XEvent ev = new XEvent();
            ev.type = XEventName.ClientMessage;
            ev.ClientMessageEvent.format = 32;
            ev.ClientMessageEvent.display = window.Display;
            ev.ClientMessageEvent.window = window.Handle;
            ev.ClientMessageEvent.ptr1 = _atom_wm_destroy;
            using (new XLock(window.Display))
            {
                Functions.XSendEvent(window.Display, window.Handle, false,
                    EventMask.NoEventMask, ref ev);
                Functions.XFlush(window.Display);
            }
        }

        #endregion

        #region public void DestroyWindow()

        public void DestroyWindow()
        {
            Debug.WriteLine("X11GLNative shutdown sequence initiated.");
            using (new XLock(window.Display))
            {
                Functions.XDestroyWindow(window.Display, window.Handle);
            }
        }

        #endregion

        #region PointToClient

        public Point PointToClient(Point point)
        {
            int ox, oy;
            IntPtr child;

            using (new XLock(window.Display))
            {
                Functions.XTranslateCoordinates(window.Display, window.RootWindow, window.Handle, point.X, point.Y, out ox, out oy, out child);
            }

            point.X = ox;
            point.Y = oy;

            return point;
        }

        #endregion

        #region PointToScreen

        public Point PointToScreen(Point point)
        {
            int ox, oy;
            IntPtr child;

            using (new XLock(window.Display))
            {
                Functions.XTranslateCoordinates(window.Display, window.Handle, window.RootWindow, point.X, point.Y, out ox, out oy, out child);
            }

            point.X = ox;
            point.Y = oy;

            return point;
        }

        #endregion

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool manuallyCalled)
        {
            if (!disposed)
            {
                if (manuallyCalled)
                {
                    if (window != null && window.Handle != IntPtr.Zero)
                    {
                        if (Exists)
                        {
                            using (new XLock(window.Display))
                            {
                                Functions.XFreeCursor(window.Display, EmptyCursor);
                                Functions.XDestroyWindow(window.Display, window.Handle);
                            }

                            while (Exists)
                                ProcessEvents();
                        }

                        window.Dispose();
                        window = null;
                    }
                }
                else
                {
                    Debug.Print("[Warning] {0} leaked.", this.GetType().Name);
                }
                disposed = true;
            }
        }

        ~X11GLNative()
        {
            this.Dispose(false);
        }

        #endregion
    }
}
