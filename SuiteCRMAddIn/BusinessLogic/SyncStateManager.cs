﻿/**
 * Outlook integration for SuiteCRM.
 * @package Outlook integration for SuiteCRM
 * @copyright SalesAgility Ltd http://www.salesagility.com
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU LESSER GENERAL PUBLIC LICENCE as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU LESSER GENERAL PUBLIC LICENCE
 * along with this program; if not, see http://www.gnu.org/licenses
 * or write to the Free Software Foundation,Inc., 51 Franklin Street,
 * Fifth Floor, Boston, MA 02110-1301  USA
 *
 * @author SalesAgility <info@salesagility.com>
 */
namespace SuiteCRMAddIn.BusinessLogic
{
    using Exceptions;
    using Extensions;
    using SuiteCRMClient.Logging;
    using SuiteCRMClient.RESTObjects;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Runtime.InteropServices;
    using Outlook = Microsoft.Office.Interop.Outlook;

    /// <summary>
    /// One of the problems with the design of the add-in is that we're trying to shim two 
    /// types of CRM entities (Calls, Meetings) onto one type of Outlook entity (Appointments).
    /// We need to treat them separately, and for that reason we need different sync state 
    /// classes for Calls and Meetings.
    /// </summary>
    public class SyncStateManager
    {
        /// <summary>
        /// The name of the modified date synchronisation property, which 
        /// should be updated to the date/time the item was most recently modified in Outlook or in CRM.
        /// </summary>
        public const string ModifiedDatePropertyName = "SOModifiedDate";

        /// <summary>
        /// The name of the type synchronisation property.
        /// </summary>
        public const string TypePropertyName = "SType";

        /// <summary>
        /// The name of the CRM ID synchronisation property.
        /// </summary>
        /// <see cref="SuiteCRMAddIn.Extensions.MailItemExtensions.CrmIdPropertyName"/> 
        public const string CrmIdPropertyName = "SEntryID";

        /// <summary>
        /// If set, don't sync with CRM.
        /// </summary>
        public const string CRMShouldNotSyncPropertyName = "ShouldNotSyncWithCRM";

        /// <summary>
        /// My underlying instance.
        /// </summary>
        private static readonly Lazy<SyncStateManager> lazy =
            new Lazy<SyncStateManager>(() => new SyncStateManager());


        /// <summary>
        /// A lock on creating new items.
        /// </summary>
        private object creationLock = new object();

        /// <summary>
        /// A log, to log stuff to.
        /// </summary>
        private ILogger log = Globals.ThisAddIn.Log;

        /// <summary>
        /// A public accessor for my instance.
        /// </summary>
        public static SyncStateManager Instance { get { return lazy.Value; } }

        /// <summary>
        /// A dictionary of all known sync states indexed by outlook id.
        /// </summary>
        private ConcurrentDictionary<string, SyncState> byOutlookId = new ConcurrentDictionary<string, SyncState>();

        /// <summary>
        /// A dictionary of sync states indexed by crm id, where known.
        /// </summary>
        private ConcurrentDictionary<string, SyncState> byCrmId = new ConcurrentDictionary<string, SyncState>();

        /// <summary>
        /// A dictionary of sync states indexed by the values of distinct fields.
        /// </summary>
        private ConcurrentDictionary<string, SyncState> byDistinctFields = new ConcurrentDictionary<string, SyncState>();

        private SyncStateManager() { }


        /// <summary>
        /// This is part of an attempt to stop the 'do you want to save' popups; save
        /// everything we've touched, whether or not we've set anything on it.
        /// </summary>
        public void BruteForceSaveAll()
        {
            foreach (SyncState state in GetSynchronisedItems())
            {
                try
                {
                    string typeName = state.GetType().Name;

                    switch (typeName)
                    {
                        // TODO: there's almost certainly a cleaner and safer way of despatching this.
                        case "CallSyncState":
                        case "MeetingSyncState":
                            (state as AppointmentSyncState).OutlookItem.Save();
                            break;
                        case "ContactSyncState":
                            (state as ContactSyncState).OutlookItem.Save();
                            break;
                        case "TaskSyncState":
                            (state as TaskSyncState).OutlookItem.Save();
                            break;
                        default:
                            Globals.ThisAddIn.Log.AddEntry($"Unexpected type {typeName} in BruteForceSaveAll",
                                SuiteCRMClient.Logging.LogEntryType.Error);
                            break;
                    }
                }
                catch (Exception)
                {
                    // should log it but not critical.
                }
            }
        }

        /// <summary>
        /// Get all the syncstates I am holding.
        /// </summary>
        /// <returns>A collection of the items which I hold which are of the specified type.</returns>
        internal ICollection<SyncState> GetSynchronisedItems()
        {
            return this.byOutlookId.Values.ToList().AsReadOnly();
        }


        /// <summary>
        /// Get all the syncstates I am holding which are of this type.
        /// </summary>
        /// <typeparam name="SyncStateType">The type which is requested.</typeparam>
        /// <returns>A collection of the items which I hold which are of the specified type.</returns>
        internal ICollection<SyncStateType> GetSynchronisedItems<SyncStateType>() where SyncStateType : SyncState
        {
            return this.byOutlookId.Values.Select(x => x as SyncStateType).Where(x => x != null).ToList<SyncStateType>().AsReadOnly();
        }


        /// <summary>
        /// Count the number of items I monitor.
        /// </summary>
        /// <returns>A count of the number of items I monitor.</returns>
        public int CountItems()
        {
            return byOutlookId.Values.Count();
        }


        /// <summary>
        /// Get the existing sync state for this item, if it exists and is of the appropriate
        /// type, else null.
        /// </summary>
        /// <param name="item">The item.</param>
        /// <returns>The appropriate sync state, or null if none.</returns>
        /// <exception cref="UnexpectedSyncStateClassException">if the sync state found is not of the expected class (shouldn't happen).</exception>
        public SyncState<ItemType> GetExistingSyncState<ItemType>(ItemType item)
            where ItemType : class
        {
            SyncState<ItemType> result;

            string typeName = Microsoft.VisualBasic.Information.TypeName(item);

            try
            {
                switch (typeName)
                {
                    // TODO: there's almost certainly a cleaner and safer way of despatching this.
                    case "AppointmentItem":
                        result = this.GetSyncState(item as Outlook.AppointmentItem) as SyncState<ItemType>;
                        break;
                    case "ContactItem":
                        result = this.GetSyncState(item as Outlook.ContactItem) as SyncState<ItemType>;
                        break;
                    case "TaskItem":
                        result = this.GetSyncState(item as Outlook.TaskItem) as SyncState<ItemType>;
                        break;
                    default:
                        Globals.ThisAddIn.Log.AddEntry($"Unexpected type {typeName} in GetExistingSyncState",
                            SuiteCRMClient.Logging.LogEntryType.Error);
                        result = null;
                        break;
                }
            }
            catch (KeyNotFoundException kex)
            {
                log.Warn("KeyNotFoundException in GetExistingSyncState", kex);
                result = null;
            }

            return result;
        }


        /// <summary>
        /// Get the existing sync state for this item, if it exists and is of the appropriate
        /// type, else null.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="appointment">The item.</param>
        /// <returns>The appropriate sync state, or null if none.</returns>
        /// <exception cref="UnexpectedSyncStateClassException">if the sync state found is not of the expected class (shouldn't happen).</exception>
        public AppointmentSyncState GetSyncState(Outlook.AppointmentItem appointment)
        {
            SyncState result = this.byOutlookId.ContainsKey(appointment.EntryID) ? this.byOutlookId[appointment.EntryID] : null;
            string crmId = result == null ? appointment.GetCrmId() : CheckForDuplicateSyncState(result, appointment.GetCrmId());

            if (result == null && string.IsNullOrEmpty(crmId))
            {
                result = null;
            }
            else if (result == null && this.byCrmId.ContainsKey(crmId))
            {
                result = this.byCrmId[crmId];
            }
            else if (result != null && crmId != null && this.byCrmId.ContainsKey(crmId) == false)
            {
                this.byCrmId[crmId] = result;
                result.CrmEntryId = crmId;
            }

            if (result != null && result as AppointmentSyncState == null)
            {
                throw new UnexpectedSyncStateClassException("AppointmentSyncState", result);
            }

            return result as AppointmentSyncState;
        }


        /// <summary>
        /// Get the existing sync state for this item, if it exists and is of the appropriate
        /// type, else null.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="contact">The item.</param>
        /// <returns>The appropriate sync state, or null if none.</returns>
        /// <exception cref="UnexpectedSyncStateClassException">if the sync state found is not of the expected class (shouldn't happen).</exception>
        public ContactSyncState GetSyncState(Outlook.ContactItem contact)
        {
            SyncState result = this.byOutlookId.ContainsKey(contact.EntryID) ? this.byOutlookId[contact.EntryID] : null;
            string crmId = CheckForDuplicateSyncState(result, contact.GetCrmId());

            if (result == null && string.IsNullOrEmpty(crmId))
            {
                result = null;
            }
            else if (result == null && this.byCrmId.ContainsKey(crmId))
            {
                result = this.byCrmId[crmId];
            }
            else if (result != null && crmId != null && this.byCrmId.ContainsKey(crmId) == false)
            {
                this.byCrmId[crmId] = result;
                result.CrmEntryId = crmId;
            }

            if (result != null && result as ContactSyncState == null)
            {
                throw new UnexpectedSyncStateClassException("ContactSyncState", result);
            }

            return result as ContactSyncState;
        }


        /// <summary>
        /// Get the existing sync state for this item, if it exists and is of the appropriate
        /// type, else null.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="task">The item.</param>
        /// <returns>The appropriate sync state, or null if none.</returns>
        /// <exception cref="UnexpectedSyncStateClassException">if the sync state found is not of the expected class (shouldn't happen).</exception>
        public TaskSyncState GetSyncState(Outlook.TaskItem task)
        {
            SyncState result = this.byOutlookId.ContainsKey(task.EntryID) ? this.byOutlookId[task.EntryID] : null;
            string crmId = result == null ? task.GetCrmId() : CheckForDuplicateSyncState(result, task.GetCrmId());

            if (result == null && string.IsNullOrEmpty(crmId))
            {
                result = null;  
            }
            else if (result == null && this.byCrmId.ContainsKey(crmId))
            {
                result = this.byCrmId[crmId];
            }
            else if (result != null && crmId != null && this.byCrmId.ContainsKey(crmId) == false)
            {
                this.byCrmId[crmId] = result;
                result.CrmEntryId = crmId;
            }

            if (result != null && result as TaskSyncState == null)
            {
                throw new UnexpectedSyncStateClassException("TaskSyncState", result);
            }

            return result as TaskSyncState;
        }


        /// <summary>
        /// Check whether there exists a sync state other than this state whose CRM id is 
        /// this CRM id or the CRM id of this state.
        /// </summary>
        /// <param name="state">The sync state to be checked.</param>
        /// <param name="crmId">A candidate CRM id.</param>
        /// <returns>A better guess at the CRM id.</returns>
        /// <exception cref="DuplicateSyncStateException">If a duplicate is detected.</exception>
        private string CheckForDuplicateSyncState(SyncState state, string crmId)
        {
            string result = string.IsNullOrEmpty(crmId) && state != null ? state.CrmEntryId : crmId;

            if (result != null)
            {
                SyncState byCrmState = string.IsNullOrEmpty(crmId) ?
                    null :
                    this.byCrmId.ContainsKey(crmId) ? this.byCrmId[crmId] : null;

                if (state != null && byCrmState != null && state != byCrmState)
                {
                    throw new DuplicateSyncStateException(state);
                }
            }

            return result;
        }


        /// <summary>
        /// Get the existing sync state for this CRM item, if it exists, else null.
        /// </summary>
        /// <param name="crmItem">The item.</param>
        /// <returns>The appropriate sync state, or null if none.</returns>
        public SyncState GetExistingSyncState(EntryValue crmItem)
        {
            SyncState result;
            try
            {
                result = this.byCrmId[crmItem.id];
            }
            catch (KeyNotFoundException)
            {
                try
                {
                    var outlookId = crmItem.GetValueAsString("outlook_id");

                    result = string.IsNullOrEmpty(outlookId) ? null : this.byOutlookId[outlookId];
                }
                catch (KeyNotFoundException)
                {
                    string distinctFields = GetDistinctFields(crmItem);

                    if (string.IsNullOrEmpty(distinctFields))
                    {
                        result = null;
                    }
                    else if (this.byDistinctFields.ContainsKey(distinctFields))
                    {
                        result = this.byDistinctFields[distinctFields];
                    }
                    else
                    {
                        result = null;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get a string representing the values of the distinct fields of this crmItem, 
        /// as a final fallback for identifying an otherwise unidentifiable object.
        /// </summary>
        /// <param name="crmItem">An item received from CRM.</param>
        /// <returns>An identifying string.</returns>
        /// <see cref="SyncState{ItemType}.IdentifyingFields"/> 
        private string GetDistinctFields(EntryValue crmItem)
        {
            string result;

            switch(crmItem.module_name)
            {
                case CallsSynchroniser.CrmModule:
                    result = CallSyncState.GetDistinctFields(crmItem);
                    break;
                case ContactSynchroniser.CrmModule:
                    result = ContactSyncState.GetDistinctFields(crmItem);
                    break;
                case MeetingsSynchroniser.CrmModule:
                    result = MeetingSyncState.GetDistinctFields(crmItem);
                    break;
                case TaskSynchroniser.CrmModule:
                    result = TaskSyncState.GetDistinctFields(crmItem);
                    break;
                default:
                    this.log.Warn($"Unexpected CRM module name '{crmItem.module_name}'");
                    result = string.Empty;
                    break;
            }

            return result;
        }


        /// <summary>
        /// Get a sync state for this item, creating it if necessary.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy despatch.</remarks>
        /// <param name="item">the item.</param>
        /// <returns>an appropriate sync state.</returns>
        /// <exception cref="UnexpectedSyncStateClassException">if the sync state found is not of the expected class (shouldn't happen).</exception>
        public SyncState<ItemType> GetOrCreateSyncState<ItemType>(ItemType item)
            where ItemType : class
        {
            lock (this.creationLock)
            {
                SyncState<ItemType> result = this.GetExistingSyncState(item);

                if (result == null)
                result = CreateSyncStateForItem(item);

                return result;
            }
        }

        private SyncState<ItemType> CreateSyncStateForItem<ItemType>(ItemType item)
            where ItemType : class
        {
            SyncState<ItemType> result;
            string outlookId;
            var typeName = Microsoft.VisualBasic.Information.TypeName(item);

            switch (typeName)
            {
                // TODO: there's almost certainly a cleaner and safer way of despatching this.
                case "AppointmentItem":
                    outlookId = ((Outlook.AppointmentItem)item).EntryID;
                    result = this.CreateSyncState(item as Outlook.AppointmentItem) as SyncState<ItemType>;
                    break;
                case "ContactItem":
                    outlookId = ((Outlook.ContactItem)item).EntryID;
                    result = this.CreateSyncState(item as Outlook.ContactItem) as SyncState<ItemType>;
                    break;
                case "TaskItem":
                    outlookId = ((Outlook.TaskItem)item).EntryID;
                    result = this.CreateSyncState(item as Outlook.TaskItem) as SyncState<ItemType>;
                    break;
                default:
                    Globals.ThisAddIn.Log.AddEntry($"Unexpected type {typeName} in GetOrCreateSyncState",
                        SuiteCRMClient.Logging.LogEntryType.Error);
                    result = null;
                    break;
            }

            if (result != null)
            {
                this.byDistinctFields[result.IdentifyingFields] = result;
            }

            return result;
        }


        /// <summary>
        /// Create an appropriate sync state for an appointment item.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="appointment">The item.</param>
        /// <returns>An appropriate sync state.</returns>
        private AppointmentSyncState CreateSyncState(Outlook.AppointmentItem appointment)
        {
            string crmId = appointment.GetCrmId();

            AppointmentSyncState result;

            if (!string.IsNullOrEmpty(crmId) && this.byCrmId.ContainsKey(crmId) && this.byCrmId[crmId] != null)
            {
                result = CheckUnexpectedFoundState<Outlook.AppointmentItem, AppointmentSyncState>(appointment, crmId);
            }
            else
            {
                var modifiedDate = ParseDateTimeFromUserProperty(appointment.UserProperties[ModifiedDatePropertyName]);
                if (appointment.IsCall())
                {
                    result = this.SetByOutlookId<AppointmentSyncState>(appointment.EntryID,
                        new CallSyncState(appointment, crmId, modifiedDate));
                }
                else
                {
                    result = this.SetByOutlookId<AppointmentSyncState>(appointment.EntryID,
                        new MeetingSyncState(appointment, crmId, modifiedDate));
                }
            }

            if (result != null && !string.IsNullOrEmpty(crmId))
            {
                this.byCrmId[crmId] = result;
            }
            
            return result;
        }


        /// <summary>
        /// Create an appropriate sync state for an contact item.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="contact">The item.</param>
        /// <returns>An appropriate sync state.</returns>
        private ContactSyncState CreateSyncState(Outlook.ContactItem contact)
        {
            string crmId = contact.GetCrmId();
            ContactSyncState result;

            if (!string.IsNullOrEmpty(crmId) && this.byCrmId.ContainsKey(crmId) && this.byCrmId[crmId] != null)
            {
                result = CheckUnexpectedFoundState<Outlook.ContactItem, ContactSyncState>(contact, crmId);
            }
            else
            {
                result = this.SetByOutlookId<ContactSyncState>(contact.EntryID,
                    new ContactSyncState(contact, crmId,
                        ParseDateTimeFromUserProperty(contact.UserProperties[ModifiedDatePropertyName])));
            }

            if (result != null && !string.IsNullOrEmpty(crmId))
            {
                this.byCrmId[crmId] = result;
            }

            return result;
        }


        /// <summary>
        /// Create an appropriate sync state for an task item.
        /// </summary>
        /// <remarks>Outlook items are not true objects and don't have a common superclass, 
        /// so we have to use this rather clumsy overloading.</remarks>
        /// <param name="task">The item.</param>
        /// <returns>An appropriate sync state.</returns>
        private TaskSyncState CreateSyncState(Outlook.TaskItem task)
        {
            string crmId = task.GetCrmId();

            TaskSyncState result;

            if (!string.IsNullOrEmpty(crmId) && this.byCrmId.ContainsKey(crmId) && this.byCrmId[crmId] != null)
            {
                result = CheckUnexpectedFoundState<Outlook.TaskItem, TaskSyncState>(task, crmId);
            }
            else
            {
                result = this.SetByOutlookId<TaskSyncState>(task.EntryID,
                    new TaskSyncState(task, crmId,
                        ParseDateTimeFromUserProperty(task.UserProperties[ModifiedDatePropertyName])));
            }

            if (result != null && !string.IsNullOrEmpty(crmId))
            {
                this.byCrmId[crmId] = result;
            }

            return result;
        }


        /// <summary>
        /// Perform sanity checks on an unexpected sync state has been found where we expected 
        /// to find none. 
        /// </summary>
        /// <remarks>This probably shouldn't happen and perhaps ought to be flagged as a hard error
        /// anyway.</remarks>
        /// <typeparam name="ItemType">The type of Outlook item being considered.</typeparam>
        /// <typeparam name="StateType">The appropriate sync state class for that item.</typeparam>
        /// <param name="olItem">The Outlook item being considered.</param>
        /// <param name="crmId">The CRM id associated with that item.</param>
        /// <returns>An appropriate sync state</returns>
        /// <exception cref="Exception">If the sync state doesn't exactly match what we would expect.</exception>
        private StateType CheckUnexpectedFoundState<ItemType, StateType>(ItemType olItem, string crmId)
            where ItemType : class
            where StateType : SyncState<ItemType>
        {
            StateType result;
            var state = this.byCrmId.ContainsKey(crmId) ? this.byCrmId[crmId] : null;

            if (state != null)
            {
                result = state as StateType;

                if (result == null)
                {
                    throw new Exception($"Unexpected state type found: {state.GetType().Name}.");
                }
                else if (!result.OutlookItem.Equals(olItem))
                {
                    throw new ProbableDuplicateItemException<ItemType>(olItem, $"Probable duplicate Outlook item; crmId is {crmId}; identifying fields are {result.IdentifyingFields}");
                }
            }
            else
            {
                result = this.CreateSyncStateForItem(olItem) as StateType;
            }

            return result;
        }


        /// <summary>
        /// Set the value of my <see cref="byOutlookId"/> dictionary for this key to this value, 
        /// provided it is not already set.
        /// </summary>
        /// <remarks>
        /// This is part of defence against ending up with duplicate SyncStates, which is an 
        /// exceedingly bad thing. Values should not be put into the <see cref="byOutlookId"/> 
        /// dictionary except through this method. 
        /// </remarks>
        /// <typeparam name="StateType">The type of <see cref="SyncState"/> I am passing.</typeparam>
        /// <param name="key">The key I am setting.</param>
        /// <param name="value">The value I am seeking to set it to.</param>
        /// <returns>The value that is set.</returns>
        private StateType SetByOutlookId<StateType>(string key, StateType value)
            where StateType : SyncState
        {
            StateType result;
            try
            {
                var current = this.byOutlookId[key];
                result = current as StateType;
            }
            catch (KeyNotFoundException)
            {
                this.byOutlookId[key] = value;
                result = value;
            }

            return result;
        }


        /// <summary>
        /// Parse a date/time from the supplied user property, if any.
        /// </summary>
        /// <param name="property">The property (may be null)</param>
        /// <returns>A date/time.</returns>
        private DateTime ParseDateTimeFromUserProperty(Outlook.UserProperty property)
        {
            DateTime result;
            if (property == null || property.Value == null)
            {
                result = default(DateTime);
            }
            else
            {
                result = DateTime.UtcNow;
                if (!DateTime.TryParseExact(property.Value, "yyyy-MM-dd HH:mm:ss", null, DateTimeStyles.None, out result))
                {
                    DateTime.TryParse(property.Value, out result);
                }
            }

            return result;
        }


        internal void RemoveOutlookId(string outlookItemEntryId)
        {
            this.byOutlookId[outlookItemEntryId] = null;
        }


        /// <summary>
        /// Remove all references to this sync state, if I hold any.
        /// </summary>
        /// <param name="state">The state to remove.</param>
        internal void RemoveSyncState(SyncState state)
        {
            lock (this.creationLock)
            {
                SyncState ignore;
                try
                {
                    if (this.byOutlookId[state.OutlookItemEntryId] == state)
                    {
                        this.byOutlookId.TryRemove(state.OutlookItemEntryId, out ignore);
                    }
                }
                catch (KeyNotFoundException) { }
                catch (COMException) { }
                try
                {
                    if (!string.IsNullOrEmpty(state.CrmEntryId) && this.byCrmId[state.CrmEntryId] == state)
                    {
                        this.byCrmId.TryRemove(state.CrmEntryId, out ignore);
                    }
                }
                catch (KeyNotFoundException) { }
            }
        }


        /// <summary>
        /// Called after a new item has been created from a CRM item, to fix up the index and prevent duplication.
        /// </summary>
        /// <typeparam name="SyncStateType">The type of <see cref="SyncState"/> passed</typeparam>
        /// <param name="crmId">The crmId to index this sync state to.</param>
        /// <param name="syncState">the sync state to index.</param>
        internal void SetByCrmId<SyncStateType>(string crmId, SyncStateType syncState) where SyncStateType : SyncState
        {
            this.byCrmId[crmId] = syncState;
            string outlookId = syncState?.OutlookItemEntryId;

            if (!string.IsNullOrEmpty(outlookId))
            {
                this.byOutlookId[outlookId] = syncState;
            }
        }
    }
}