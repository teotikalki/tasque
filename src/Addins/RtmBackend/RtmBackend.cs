// RtmBackend.cs created with MonoDevelop
// User: boyd at 7:10 AM 2/11/2008
//
// To change standard headers go to Edit->Preferences->Coding->Standard Headers
//

using System;
using Mono.Unix;
using Tasque.Backends;
using RtmNet;
using System.Threading;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Tasque.Backends.RtmBackend
{
	public class RtmBackend : Backend
	{
		private const string apiKey = "b29f7517b6584035d07df3170b80c430";
		private const string sharedSecret = "93eb5f83628b2066";

		private Gtk.ListStore categoryListStore;
		private Gtk.TreeModelSort sortedCategoriesModel;
		
		private Thread refreshThread;
		private bool runningRefreshThread;
		private AutoResetEvent runRefreshEvent;

		private Rtm rtm;
		private string frob;
		private Auth rtmAuth;
		private string timeline;

		private object taskLock;

		private Dictionary<string, RtmCategory> categories;
		private object catLock;
		private bool initialized;
		private bool configured;

		public event BackendInitializedHandler BackendInitialized;
		public event BackendSyncStartedHandler BackendSyncStarted;
		public event BackendSyncFinishedHandler BackendSyncFinished;
		
		public RtmBackend ()
		{
			initialized = false;
			configured = false;

			taskLock = new Object();
			
			categories = new Dictionary<string, RtmCategory> ();
			catLock = new Object();

			// *************************************
			// Data Model Set up
			// *************************************
			Tasks = new ObservableCollection<Task>();

			categoryListStore = new Gtk.ListStore (typeof (Category));

			sortedCategoriesModel = new Gtk.TreeModelSort (categoryListStore);
			sortedCategoriesModel.SetSortFunc (0, new Gtk.TreeIterCompareFunc (CompareCategorySortFunc));
			sortedCategoriesModel.SetSortColumnId (0, Gtk.SortType.Ascending);

			// make sure we have the all Category in our list
			Gtk.Application.Invoke ( delegate {
				AllCategory allCategory = new Tasque.AllCategory ();
				Gtk.TreeIter iter = categoryListStore.Append ();
				categoryListStore.SetValue (iter, 0, allCategory);				
			});

			runRefreshEvent = new AutoResetEvent(false);
			
			runningRefreshThread = false;
			refreshThread  = new Thread(RefreshThreadLoop);
		}

		#region Public Properties
		public string Name
		{
			get { return "Remember the Milk"; }
		}
		
		/// <value>
		/// All the tasks including ITaskDivider items.
		/// </value>
		public IEnumerable<Task> Tasks
		{
			get { return Tasks.OrderBy (t => t, Comparer<Task>.Default); }
		}

		public ObservableCollection<Task> Tasks { get; private set; }

		/// <value>
		/// This returns all the task lists (categories) that exist.
		/// </value>
		public Gtk.TreeModel Categories2
		{
			get { return sortedCategoriesModel; }
		}

		public string RtmUserName
		{
			get {
				if( (rtmAuth != null) && (rtmAuth.User != null) ) {
					return rtmAuth.User.Username;
				} else
					return null;
			}
		}
		
		/// <value>
		/// Indication that the rtm backend is configured
		/// </value>
		public bool Configured
		{
			get { return configured; }
		}
		
		/// <value>
		/// Inidication that the backend is initialized
		/// </value>
		public bool Initialized
		{
			get { return initialized; }
		}
		
#endregion // Public Properties

#region Public Methods
		public Task CreateTask (string taskName, Category category)
		{
			string categoryID;
			RtmTask rtmTask = null;
			
			if(category is Tasque.AllCategory)
				categoryID = null;
			else
				categoryID = (category as RtmCategory).ID;	

			if(rtm != null) {
				try {
					List list;
					
					if(categoryID == null)
						list = rtm.TasksAdd(timeline, taskName);
					else
						list = rtm.TasksAdd(timeline, taskName, categoryID);

					rtmTask = UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set create task: " + taskName);
					Debug.WriteLine(e.ToString());
				}
			}
			else
				throw new Exception("Unable to communicate with Remember The Milk");
				
			return rtmTask;
		}
		
		public void DeleteTask(Task task)
		{
			RtmTask rtmTask = task as RtmTask;
			if(rtm != null) {
				try {
					rtm.TasksDelete(timeline, rtmTask.ListID, rtmTask.SeriesTaskID, rtmTask.TaskTaskID);

					lock(taskLock)
					{
						Gtk.Application.Invoke ( delegate {
							if(taskIters.ContainsKey(rtmTask.ID)) {
								Gtk.TreeIter iter = taskIters[rtmTask.ID];
								Tasks.Remove(ref iter);
								taskIters.Remove(rtmTask.ID);
							}
						});
					}
				} catch(Exception e) {
					Debug.WriteLine("Unable to delete task: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}
			else
				throw new Exception("Unable to communicate with Remember The Milk");
		}
		
		public void Refresh()
		{
			Debug.WriteLine("Refreshing data...");

			if (!runningRefreshThread)
				StartThread();

			runRefreshEvent.Set();
			
			Debug.WriteLine("Done refreshing data!");
		}

		public void Initialize()
		{
			// *************************************
			// AUTHENTICATION to Remember The Milk
			// *************************************
			string authToken =
				Application.Preferences.Get (Preferences.AuthTokenKey);
			if (authToken != null ) {
				Debug.WriteLine("Found AuthToken, checking credentials...");
				try {
					rtm = new Rtm(apiKey, sharedSecret, authToken);
					rtmAuth = rtm.AuthCheckToken(authToken);
					timeline = rtm.TimelineCreate();
					Debug.WriteLine("RTM Auth Token is valid!");
					Debug.WriteLine("Setting configured status to true");
					configured = true;
				} catch (RtmNet.RtmApiException e) {
					
					Application.Preferences.Set (Preferences.AuthTokenKey, null);
					Application.Preferences.Set (Preferences.UserIdKey, null);
					Application.Preferences.Set (Preferences.UserNameKey, null);
					rtm = null;
					rtmAuth = null;
					Trace.TraceError("Exception authenticating, reverting" + e.Message);
				} 			
				catch (RtmNet.RtmWebException e) {
					rtm = null;
					rtmAuth = null;
					Trace.TraceError("Not connected to RTM, maybe proxy: #{0}", e.Message);
 				}
				catch (System.Net.WebException e) {
					rtm = null;
					rtmAuth = null;
					Trace.TraceError("Problem connecting to internet: #{0}", e.Message);
				}
			}

			if(rtm == null)
				rtm = new Rtm(apiKey, sharedSecret);
	
			StartThread();	
		}

		public void StartThread()
		{
			if (!configured) {
				Debug.WriteLine("Backend not configured, not starting thread");
				return;
			}
			runningRefreshThread = true;
			Debug.WriteLine("ThreadState: " + refreshThread.ThreadState);
			if (refreshThread.ThreadState == ThreadState.Running) {
				Debug.WriteLine ("RtmBackend refreshThread already running");
			} else {
				if (!refreshThread.IsAlive) {
					refreshThread  = new Thread(RefreshThreadLoop);
				}
				refreshThread.Start();
			}
			runRefreshEvent.Set();
		}

		public void Cleanup()
		{
			runningRefreshThread = false;
			runRefreshEvent.Set();
			refreshThread.Abort ();
		}

		public Gtk.Widget GetPreferencesWidget ()
		{
			return new RtmPreferencesWidget ();
		}

		public string GetAuthUrl()
		{
			frob = rtm.AuthGetFrob();
			string url = rtm.AuthCalcUrl(frob, AuthLevel.Delete);
			return url;
		}

		public void FinishedAuth()
		{
			rtmAuth = rtm.AuthGetToken(frob);
			if (rtmAuth != null) {
				Preferences prefs = Application.Preferences;
				prefs.Set (Preferences.AuthTokenKey, rtmAuth.Token);
				if (rtmAuth.User != null) {
					prefs.Set (Preferences.UserNameKey, rtmAuth.User.Username);
					prefs.Set (Preferences.UserIdKey, rtmAuth.User.UserId);
				}
			}
			
			string authToken =
				Application.Preferences.Get (Preferences.AuthTokenKey);
			if (authToken != null ) {
				Debug.WriteLine("Found AuthToken, checking credentials...");
				try {
					rtm = new Rtm(apiKey, sharedSecret, authToken);
					rtmAuth = rtm.AuthCheckToken(authToken);
					timeline = rtm.TimelineCreate();
					Debug.WriteLine("RTM Auth Token is valid!");
					Debug.WriteLine("Setting configured status to true");
					configured = true;
					Refresh();
				} catch (Exception e) {
					rtm = null;
					rtmAuth = null;				
					Trace.TraceError("Exception authenticating, reverting" + e.Message);
				}	
			}
		}

		public void UpdateTaskName(RtmTask task)
		{
			if(rtm != null) {
				try {
					List list = rtm.TasksSetName(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID, task.Name);		
					UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set name on task: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}
		}
		
		public void UpdateTaskDueDate(RtmTask task)
		{
			if(rtm != null) {
				try {
					List list;
					if(task.DueDate == DateTime.MinValue)
						list = rtm.TasksSetDueDate(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID);
					else	
						list = rtm.TasksSetDueDate(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID, task.DueDateString);
					UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set due date on task: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}
		}
		
		public void UpdateTaskCompleteDate(RtmTask task)
		{
			UpdateTask(task);
		}
		
		public void UpdateTaskPriority(RtmTask task)
		{
			if(rtm != null) {
				try {
					List list = rtm.TasksSetPriority(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID, task.PriorityString);
					UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set priority on task: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}
		}
		
		public void UpdateTaskActive(RtmTask task)
		{
			if(task.State == TaskState.Completed)
			{
				if(rtm != null) {
					try {
						List list = rtm.TasksUncomplete(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID);
						UpdateTaskFromResult(list);
					} catch(Exception e) {
						Debug.WriteLine("Unable to set Task as completed: " + task.Name);
						Debug.WriteLine(e.ToString());
					}
				}
			}
			else
				UpdateTask(task);
		}
		
		public void UpdateTaskInactive(RtmTask task)
		{
			UpdateTask(task);
		}	

		public void UpdateTaskCompleted(RtmTask task)
		{
			if(rtm != null) {
				try {
					List list = rtm.TasksComplete(timeline, task.ListID, task.SeriesTaskID, task.TaskTaskID);
					UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set Task as completed: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}
		}	

		public void UpdateTaskDeleted(RtmTask task)
		{
			UpdateTask(task);
		}
		

		public void MoveTaskCategory(RtmTask task, string id)
		{
			if(rtm != null) {
				try {
					List list = rtm.TasksMoveTo(timeline, task.ListID, id, task.SeriesTaskID, task.TaskTaskID);
					UpdateTaskFromResult(list);
				} catch(Exception e) {
					Debug.WriteLine("Unable to set Task as completed: " + task.Name);
					Debug.WriteLine(e.ToString());
				}
			}					
		}
		
		
		public void UpdateTask(RtmTask task)
		{
			lock(taskLock)
			{
				Gtk.TreeIter iter;
				
				Gtk.Application.Invoke ( delegate {
					if(taskIters.ContainsKey(task.ID)) {
						iter = taskIters[task.ID];
						Tasks.SetValue (iter, 0, task);
					}
				});
			}		
		}
		
		public RtmTask UpdateTaskFromResult(List list)
		{
			TaskSeries ts = list.TaskSeriesCollection[0];
			if(ts != null) {
				RtmTask rtmTask = new RtmTask(ts, this, list.ID);
				lock(taskLock)
				{
					Gtk.Application.Invoke ( delegate {
						if(taskIters.ContainsKey(rtmTask.ID)) {
							Gtk.TreeIter iter = taskIters[rtmTask.ID];
							Tasks.SetValue (iter, 0, rtmTask);
						} else {
							Gtk.TreeIter iter = Tasks.AppendNode();
							taskIters.Add(rtmTask.ID, iter);
							Tasks.SetValue (iter, 0, rtmTask);
						}
					});
				}
				return rtmTask;				
			}
			return null;
		}
		
		public RtmCategory GetCategory(string id)
		{
			if(categories.ContainsKey(id))
				return categories[id];
			else
				return null;
		}
		
		public RtmNote CreateNote (RtmTask rtmTask, string text)
		{
			RtmNet.Note note = null;
			RtmNote rtmNote = null;
			
			if(rtm != null) {
				try {
					note = rtm.NotesAdd(timeline, rtmTask.ListID, rtmTask.SeriesTaskID, rtmTask.TaskTaskID, String.Empty, text);
					rtmNote = new RtmNote(note);
				} catch(Exception e) {
					Debug.WriteLine("RtmBackend.CreateNote: Unable to create a new note");
					Debug.WriteLine(e.ToString());
				}
			}
			else
				throw new Exception("Unable to communicate with Remember The Milk");
				
			return rtmNote;
		}


		public void DeleteNote (RtmTask rtmTask, RtmNote note)
		{
			if(rtm != null) {
				try {
					rtm.NotesDelete(timeline, note.ID);
				} catch(Exception e) {
					Debug.WriteLine("RtmBackend.DeleteNote: Unable to delete note");
					Debug.WriteLine(e.ToString());
				}
			}
			else
				throw new Exception("Unable to communicate with Remember The Milk");
		}

		public void SaveNote (RtmTask rtmTask, RtmNote note)
		{
			if(rtm != null) {
				try {
					rtm.NotesEdit(timeline, note.ID, String.Empty, note.Text);
				} catch(Exception e) {
					Debug.WriteLine("RtmBackend.SaveNote: Unable to save note");
					Debug.WriteLine(e.ToString());
				}
			}
			else
				throw new Exception("Unable to communicate with Remember The Milk");
		}

#endregion // Public Methods

#region Private Methods

		/// <summary>
		/// Update the model to match what is in RTM
		/// FIXME: This is a lame implementation and needs to be optimized
		/// </summary>		
		private void UpdateCategories(Lists lists)
		{
			Debug.WriteLine("RtmBackend.UpdateCategories was called");
			
			try {
				foreach(List list in lists.listCollection)
				{
					RtmCategory rtmCategory = new RtmCategory(list);

					lock(catLock)
					{
						Gtk.TreeIter iter;
						
						Gtk.Application.Invoke ( delegate {

							if(categories.ContainsKey(rtmCategory.ID)) {
								iter = categories[rtmCategory.ID].Iter;
								categoryListStore.SetValue (iter, 0, rtmCategory);
							} else {
								iter = categoryListStore.Append();
								categoryListStore.SetValue (iter, 0, rtmCategory);
								rtmCategory.Iter = iter;
								categories.Add(rtmCategory.ID, rtmCategory);
							}
						});
					}
				}
			} catch (Exception e) {
				Debug.WriteLine("Exception in fetch " + e.Message);
			}
			Debug.WriteLine("RtmBackend.UpdateCategories is done");			
		}

		/// <summary>
		/// Update the model to match what is in RTM
		/// FIXME: This is a lame implementation and needs to be optimized
		/// </summary>		
		private void UpdateTasks(Lists lists)
		{
			Debug.WriteLine("RtmBackend.UpdateTasks was called");
			
			try {
				foreach(List list in lists.listCollection)
				{
					Tasks tasks = null;
					try {
						tasks = rtm.TasksGetList(list.ID);
					} catch (Exception tglex) {
						Debug.WriteLine("Exception calling TasksGetList(list.ListID) " + tglex.Message);
					}

					if(tasks != null) {
						foreach(List tList in tasks.ListCollection)
						{
							if (tList.TaskSeriesCollection == null)
								continue;
							foreach(TaskSeries ts in tList.TaskSeriesCollection)
							{
								RtmTask rtmTask = new RtmTask(ts, this, list.ID);
								
								lock(taskLock)
								{
									Gtk.TreeIter iter;
									
									Gtk.Application.Invoke ( delegate {

										if(taskIters.ContainsKey(rtmTask.ID)) {
											iter = taskIters[rtmTask.ID];
										} else {
											iter = Tasks.AppendNode ();
											taskIters.Add(rtmTask.ID, iter);
										}

										Tasks.SetValue (iter, 0, rtmTask);
									});
								}
							}
						}
					}
				}
			} catch (Exception e) {
				Debug.WriteLine("Exception in fetch " + e.Message);
				Debug.WriteLine(e.ToString());
			}
			Debug.WriteLine("RtmBackend.UpdateTasks is done");			
		}
		
		
		
		private void RefreshThreadLoop()
		{
			while(runningRefreshThread) {
				runRefreshEvent.WaitOne();

				if(!runningRefreshThread)
					return;

				// Fire the event on the main thread
				Gtk.Application.Invoke ( delegate {
					if(BackendSyncStarted != null)
						BackendSyncStarted();
				});

				runRefreshEvent.Reset();

				if(rtmAuth != null) {
					Lists lists = rtm.ListsGetList();
					UpdateCategories(lists);
					UpdateTasks(lists);
				}
				if(!initialized) {
					initialized = true;

					// Fire the event on the main thread
					Gtk.Application.Invoke ( delegate {
						if(BackendInitialized != null)
							BackendInitialized();
					});
				}

				// Fire the event on the main thread
				Gtk.Application.Invoke ( delegate {
					if(BackendSyncFinished != null)
						BackendSyncFinished();
				});
			}
		}
		
#endregion // Private Methods

#region Event Handlers
#endregion // Event Handlers
	}
}
