// 
// GtkTray.cs
//  
// Author:
//       Antonius Riha <antoniusriha@gmail.com>
// 
// Copyright (c) 2012 Antonius Riha
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
using System;
using System.Text;
using Mono.Unix;
using Gtk;
using Tasque.Backends;

namespace Tasque
{
	public abstract class GtkTray : IDisposable
	{
		public static GtkTray CreateTray ()
		{
			var desktopSession = Environment.GetEnvironmentVariable ("DESKTOP_SESSION");
			GtkTray tray;
			switch (desktopSession) {
			case "ubuntu":
				tray = new AppIndicatorTray ();
				break;
			case "ubuntu-2d":
				tray = new AppIndicatorTray ();
				break;
			case "gnome-classic":
				tray = new AppIndicatorTray ();
				break;
			case "gnome-fallback":
				tray = new AppIndicatorTray ();
				break;
			default:
				tray = new StatusIconTray ();
				break;
			}
			return tray;
		}

		protected GtkTray ()
		{
			UpdateBackend ();
			Application.Instance.BackendChanged += HandleBackendChanged;
			RefreshTrayIconTooltip ();
		}

		#region IDisposable implementation
		public void Dispose ()
		{
			var app = Application.Instance;
			if (app != null)
				app.BackendChanged -= HandleBackendChanged;
		}
		#endregion
		
		public void RefreshTrayIconTooltip ()
		{
			var oldTooltip = Tooltip;
			var sb = new StringBuilder ();
			
			var overdueTasks = Application.Instance.OverdueTasks;
			if (overdueTasks != null) {
				int count = overdueTasks.IterNChildren ();

				if (count > 0) {
					sb.Append (String.Format (Catalog.GetPluralString ("{0} task is Overdue\n", "{0} tasks are Overdue\n", count), count));
				}
			}
			
			var todayTasks = Application.Instance.TodayTasks;
			if (todayTasks != null) {
				int count = todayTasks.IterNChildren ();

				if (count > 0) {
					sb.Append (String.Format (Catalog.GetPluralString ("{0} task for Today\n", "{0} tasks for Today\n", count), count));
				}
			}
			
			var tomorrowTasks = Application.Instance.TomorrowTasks;
			if (tomorrowTasks != null) {
				int count = tomorrowTasks.IterNChildren ();

				if (count > 0) {
					sb.Append (String.Format (Catalog.GetPluralString ("{0} task for Tomorrow\n", "{0} tasks for Tomorrow\n", count), count));
				}
			}

			if (sb.Length == 0) {
				// Translators: This is the status icon's tooltip. When no tasks are overdue, due today, or due tomorrow, it displays this fun message
				Tooltip = Catalog.GetString ("Tasque Rocks");
				return;
			}

			Tooltip = sb.ToString ().TrimEnd ('\n');
			
			if (Tooltip != oldTooltip)
				OnTooltipChanged ();
		}

		protected string IconName { get { return "tasque-panel"; } }
		
		protected Menu Menu {
			get {
				if (uiManager == null)
					RegisterUIManager ();
				return (Menu)uiManager.GetWidget ("/TrayIconMenu");
			}
		}
		
		protected Gtk.Action ToggleTaskWindowAction { get; private set; }

		protected string Tooltip { get; private set; }
		
		protected virtual void OnTooltipChanged () {}
		
		void HandleBackendChanged (object sender, EventArgs e)
		{
			UpdateBackend ();
		}

		void HandleBackendInitialized ()
		{
			UpdateActionSensitivity ();
		}

		void OnAbout (object sender, EventArgs args)
		{
			var authors = new string [] {
				"Boyd Timothy <btimothy@gmail.com>",
				"Calvin Gaisford <calvinrg@gmail.com>",
				"Sandy Armstrong <sanfordarmstrong@gmail.com>",
				"Brian G. Merrell <bgmerrell@novell.com>"
			};
			
			var translators = Catalog.GetString ("translator-credits");
			if (translators == "translator-credits")
				translators = null;
			
			var about = new AboutDialog ();
			about.ProgramName = "Tasque";
			about.Version = Defines.Version;
			about.Logo = Utilities.GetIcon("tasque-48", 48);
			about.Copyright = Catalog.GetString ("Copyright \xa9 2008 Novell, Inc.");
			about.Comments = Catalog.GetString ("A Useful Task List");
			about.Website = "http://live.gnome.org/Tasque";
			about.WebsiteLabel = Catalog.GetString("Tasque Project Homepage");
			about.Authors = authors;
			about.TranslatorCredits = translators;
			about.IconName = "tasque";
			about.SetSizeRequest(300, 300);
			about.Run ();
			about.Destroy ();
		}

		void RegisterUIManager ()
		{
			var trayActionGroup = new ActionGroup ("Tray");
			trayActionGroup.Add (new ActionEntry [] {
				new ActionEntry ("NewTaskAction", Stock.New, Catalog.GetString ("New Task ..."), null, null, delegate {
					// Show the TaskWindow and then cause a new task to be created
					TaskWindow.ShowWindow ();
					TaskWindow.GrabNewTaskEntryFocus ();
				}),

				new ActionEntry ("AboutAction", Stock.About, OnAbout),

				new ActionEntry ("PreferencesAction", Stock.Preferences, delegate { Application.ShowPreferences (); }),

				new ActionEntry ("RefreshAction", Stock.Execute, Catalog.GetString ("Refresh Tasks ..."),
				                 null, null, delegate { Application.Backend.Refresh(); }),

				new ActionEntry ("QuitAction", Stock.Quit, delegate { Application.Instance.Quit (); })
			});
			
			ToggleTaskWindowAction = new Gtk.Action ("ToggleTaskWindowAction", Catalog.GetString ("Toggle Task Window"));
			ToggleTaskWindowAction.ActionGroup = trayActionGroup;
			ToggleTaskWindowAction.Activated += delegate { TaskWindow.ToggleWindowVisible (); };
			
			uiManager = new UIManager ();
			uiManager.AddUiFromString (MenuXml);
			uiManager.InsertActionGroup (trayActionGroup, 0);
		}

		void UpdateActionSensitivity ()
		{
			var backend = Application.Backend;
			var backendItemsSensitive = (backend != null && backend.Initialized);
			
			if (uiManager == null)
				RegisterUIManager ();
			
			uiManager.GetAction ("/TrayIconMenu/NewTaskAction").Sensitive = backendItemsSensitive;
			uiManager.GetAction ("/TrayIconMenu/RefreshAction").Sensitive = backendItemsSensitive;
		}

		void UpdateBackend ()
		{
			if (backend != null)
				backend.BackendInitialized -= HandleBackendInitialized;
			backend = Application.Backend;

			if (backend != null)
				backend.BackendInitialized += HandleBackendInitialized;

			UpdateActionSensitivity ();
		}

		IBackend backend;
		UIManager uiManager;
		const string MenuXml = @"
<ui>
	<popup name=""TrayIconMenu"">
		<menuitem action=""NewTaskAction""/>
		<separator/>
		<menuitem action=""PreferencesAction""/>
		<menuitem action=""AboutAction""/>
		<separator/>
		<menuitem action=""RefreshAction""/>
		<separator/>
		<menuitem action=""QuitAction""/>
	</popup>
</ui>
";
	}
}
