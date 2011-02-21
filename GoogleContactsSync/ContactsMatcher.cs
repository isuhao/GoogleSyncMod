using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Google.GData.Client;
using Google.GData.Contacts;
using Google.GData.Extensions;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace GoContactSyncMod
{
	internal static class ContactsMatcher
	{
		/// <summary>
		/// Time tolerance in seconds - used when comparing date modified.
		/// </summary>
		public static int TimeTolerance = 20;

		/// <summary>
		/// Matches outlook and google contact by a) google id b) properties.
		/// </summary>
		/// <param name="outlookContacts"></param>
		/// <param name="googleContacts"></param>
		/// <returns>Returns a list of match pairs (outlook contact + google contact) for all contact. Those that weren't matche will have it's peer set to null</returns>
		public static ContactMatchList MatchContacts(Syncronizer sync, out DuplicateDataException duplicatesFound)
		{
            Logger.Log("Matching Outlook and Google contacts...", EventType.Information);
			ContactMatchList result = new ContactMatchList(Math.Max(sync.OutlookContacts.Count, sync.GoogleContacts.Capacity));
            string duplicateGoogleMatches = "";         

            sync.GoogleContactDuplicates = new Collection<ContactMatch>();

			//for each outlook contact try to get google contact id from user properties
			//if no match - try to match by properties
			//if no match - create a new match pair without google contact. 
			//foreach (Outlook._ContactItem olc in outlookContacts)
			Outlook.ContactItem olc;
            Collection<Outlook.ContactItem> outlookContactsWithoutOutlookGoogleId = new Collection<Outlook.ContactItem>();
		    #region Match first all outlookContacts by sync id
            for (int i = 1; i <= sync.OutlookContacts.Count; i++)
			{               
				olc = null;
				try
				{
					olc = sync.OutlookContacts[i] as Outlook.ContactItem;
                    if (olc == null)
                    {
                        //Logger.Log("Empty Outlook contact found", EventType.Warning);
                        continue;
                    }
				}
				catch
				{
					//this is needed because some contacts throw exceptions
					continue;
				}


				// sometimes contacts throw Exception when accessing their properties, so we give it a controlled try first.
				try
				{
					string email1Address = olc.Email1Address;
				}
				catch
				{
					string message;
					try
					{
						message = string.Format("Can't access contact details for outlook contact {0}.", olc.FileAs);
					}
					catch
					{
						message = null;
					}

					if (olc != null && message != null) // it's useless to say "we couldn't access some contacts properties
					{
						Logger.Log(message, EventType.Warning);
					}

					continue;
				}

                if (!IsContactValid(olc))
				{
					Logger.Log(string.Format("Invalid outlook contact ({0}). Skipping", olc.FileAs), EventType.Warning);
					continue;
				}

				if (olc.Body != null && olc.Body.Length > 62000)
				{
					// notes field too large
					Logger.Log(string.Format("Skipping outlook contact ({0}). Reduce the notes field to a maximum of 62.000 characters.", olc.FileAs), EventType.Warning);
                    continue;
				}
                

				//try to match this contact to one of google contacts, but only if matches shall not be reset
				Outlook.UserProperty idProp = olc.UserProperties[sync.OutlookPropertyNameId];
                if (idProp != null)
                {
                    AtomId id = new AtomId((string)idProp.Value);
                    ContactEntry foundContact = sync.GoogleContacts.FindById(id) as ContactEntry;
                    ContactMatch match = new ContactMatch(olc, null);

                    if (foundContact != null && !foundContact.Deleted)
                    {
                        //we found a match by google id, that is not deleted yet
                        match.AddGoogleContact(foundContact);
                        result.Add(match);
                        //Remove the contact from the list to not sync it twice
                        sync.GoogleContacts.Remove(foundContact);
                    }
                    else
                    {
                        //If no match found, is the contact either deleted on Google side or was a copy on Outlook side 
                        //If it is a copy on Outlook side, the idProp.Value must be emptied to assure, the contact is created on Google side and not deleted on Outlook side
                        bool matchIsDuplicate = false;
                        foreach (ContactMatch existingMatch in result)
                        {
                            if (existingMatch.OutlookContact.UserProperties[sync.OutlookPropertyNameId].Value.Equals(idProp.Value))
                            {
                                matchIsDuplicate = true;
                                idProp.Value = "";
                                outlookContactsWithoutOutlookGoogleId.Add(olc);
                                break;
                            }

                        }

                        if (!matchIsDuplicate)
                            result.Add(match);
                    }

                }
                else
                   outlookContactsWithoutOutlookGoogleId.Add(olc);
            }
            #endregion
            #region Match the remaining contacts by properties

            for (int i = 0; i <= outlookContactsWithoutOutlookGoogleId.Count-1; i++)
            {
                olc = outlookContactsWithoutOutlookGoogleId[i];

                //no match found by id => match by common properties
                //create a default match pair with just outlook contact.
                ContactMatch match = new ContactMatch(olc, null);

                //foreach google contac try to match and create a match pair if found some match(es)
                foreach (ContactEntry entry in sync.GoogleContacts)
                {
                    if (entry.Deleted)
                        continue;


                    // only match if there is either an email or telephone or else
                    // a matching google contact will be created at each sync
                    //1. try to match by FileAs
                    //1.1 try to match by FullName
                    //2. try to match by emails
                    //3. try to match by mobile phone number, don't match by home or business bumbers, because several people may share the same home or business number
                    if (!string.IsNullOrEmpty(olc.FileAs) && olc.FileAs.Equals(entry.Title.Text, StringComparison.InvariantCultureIgnoreCase) ||
                        !string.IsNullOrEmpty(olc.FullName) && olc.FullName.Equals(entry.Title.Text, StringComparison.InvariantCultureIgnoreCase) ||                        
                        !string.IsNullOrEmpty(olc.Email1Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                        !string.IsNullOrEmpty(olc.Email2Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                        !string.IsNullOrEmpty(olc.Email3Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                        olc.MobileTelephoneNumber != null && FindPhone(olc.MobileTelephoneNumber, entry.Phonenumbers) != null
                        )
                    {
                        match.AddGoogleContact(entry);
                    }                    

                }

                #region find duplicates not needed now
                //if (match.GoogleContact == null && match.OutlookContact != null)
                //{//If GoogleContact, we have to expect a conflict because of Google insert of duplicates
                //    foreach (ContactEntry entry in sync.GoogleContacts)
                //    {                        
                //        if (!string.IsNullOrEmpty(olc.FullName) && olc.FullName.Equals(entry.Title.Text, StringComparison.InvariantCultureIgnoreCase) ||
                //         !string.IsNullOrEmpty(olc.FileAs) && olc.FileAs.Equals(entry.Title.Text, StringComparison.InvariantCultureIgnoreCase) ||
                //         !string.IsNullOrEmpty(olc.Email1Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                //         !string.IsNullOrEmpty(olc.Email2Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                //         !string.IsNullOrEmpty(olc.Email3Address) && FindEmail(olc.Email1Address, entry.Emails) != null ||
                //         olc.MobileTelephoneNumber != null && FindPhone(olc.MobileTelephoneNumber, entry.Phonenumbers) != null
                //         )
                //    }
                //// check for each email 1,2 and 3 if a duplicate exists with same email, because Google doesn't like inserting new contacts with same email
                //Collection<Outlook.ContactItem> duplicates1 = new Collection<Outlook.ContactItem>();
                //Collection<Outlook.ContactItem> duplicates2 = new Collection<Outlook.ContactItem>();
                //Collection<Outlook.ContactItem> duplicates3 = new Collection<Outlook.ContactItem>();
                //if (!string.IsNullOrEmpty(olc.Email1Address))
                //    duplicates1 = sync.OutlookContactByEmail(olc.Email1Address);

                //if (!string.IsNullOrEmpty(olc.Email2Address))
                //    duplicates2 = sync.OutlookContactByEmail(olc.Email2Address);

                //if (!string.IsNullOrEmpty(olc.Email3Address))
                //    duplicates3 = sync.OutlookContactByEmail(olc.Email3Address);


                //if (duplicates1.Count > 1 || duplicates2.Count > 1 || duplicates3.Count > 1)
                //{
                //    if (string.IsNullOrEmpty(duplicatesEmailList))
                //        duplicatesEmailList = "Outlook contacts with the same email have been found and cannot be synchronized. Please delete duplicates of:";

                //    if (duplicates1.Count > 1)
                //        foreach (Outlook.ContactItem duplicate in duplicates1)
                //        {
                //            string str = olc.FileAs + " (" + olc.Email1Address + ")";
                //            if (!duplicatesEmailList.Contains(str))
                //                duplicatesEmailList += Environment.NewLine + str;
                //        }
                //    if (duplicates2.Count > 1)
                //        foreach (Outlook.ContactItem duplicate in duplicates2)
                //        {
                //            string str = olc.FileAs + " (" + olc.Email2Address + ")";
                //            if (!duplicatesEmailList.Contains(str))
                //                duplicatesEmailList += Environment.NewLine + str;
                //        }
                //    if (duplicates3.Count > 1)
                //        foreach (Outlook.ContactItem duplicate in duplicates3)
                //        {
                //            string str = olc.FileAs + " (" + olc.Email3Address + ")";
                //            if (!duplicatesEmailList.Contains(str))
                //                duplicatesEmailList += Environment.NewLine + str;
                //        }
                //    continue;
                //}
                //else if (!string.IsNullOrEmpty(olc.Email1Address))
                //{
                //    ContactMatch dup = result.Find(delegate(ContactMatch match)
                //    {
                //        return match.OutlookContact != null && match.OutlookContact.Email1Address == olc.Email1Address;
                //    });
                //    if (dup != null)
                //    {
                //        Logger.Log(string.Format("Duplicate contact found by Email1Address ({0}). Skipping", olc.FileAs), EventType.Information);
                //        continue;
                //    }
                //}

                //// check for unique mobile phone, because this sync tool uses the also the mobile phone to identify matches between Google and Outlook
                //Collection<Outlook.ContactItem> duplicatesMobile = new Collection<Outlook.ContactItem>();
                //if (!string.IsNullOrEmpty(olc.MobileTelephoneNumber))
                //    duplicatesMobile = sync.OutlookContactByProperty("MobileTelephoneNumber", olc.MobileTelephoneNumber);

                //if (duplicatesMobile.Count > 1)
                //{
                //    if (string.IsNullOrEmpty(duplicatesMobileList))
                //        duplicatesMobileList = "Outlook contacts with the same mobile phone have been found and cannot be synchronized. Please delete duplicates of:";

                //    foreach (Outlook.ContactItem duplicate in duplicatesMobile)
                //    {
                //        sync.OutlookContactDuplicates.Add(olc);
                //        string str = olc.FileAs + " (" + olc.MobileTelephoneNumber + ")";
                //        if (!duplicatesMobileList.Contains(str))
                //            duplicatesMobileList += Environment.NewLine + str;
                //    }
                //    continue;
                //}
                //else if (!string.IsNullOrEmpty(olc.MobileTelephoneNumber))
                //{
                //    ContactMatch dup = result.Find(delegate(ContactMatch match)
                //    {
                //        return match.OutlookContact != null && match.OutlookContact.MobileTelephoneNumber == olc.MobileTelephoneNumber;
                //    });
                //    if (dup != null)
                //    {
                //        Logger.Log(string.Format("Duplicate contact found by MobileTelephoneNumber ({0}). Skipping", olc.FileAs), EventType.Information);
                //        continue;
                //    }
                //}

                #endregion

                if (match.AllGoogleContactMatches == null || match.AllGoogleContactMatches.Count == 0)
                {
                    Logger.Log(string.Format("No match found for outlook contact ({0})", olc.FileAs), EventType.Information);
                }
                else
                {
                    //Remember Google duplicates to later react to it when resetting matches or syncing
                    //ResetMatches: Also reset the duplicates
                    //Sync: Skip duplicates (don't sync duplicates to be fail safe)
                    if (match.AllGoogleContactMatches.Count > 1)
                    {
                        sync.GoogleContactDuplicates.Add(match);
                    }

                    foreach (ContactEntry entry in match.AllGoogleContactMatches)
                    {
                        //Remove matched google contacts from list to not match it twice
                        sync.GoogleContacts.Remove(entry);

                        if (match.AllGoogleContactMatches.Count > 1)
                        {//Create message for duplicatesFound exception
                            if (string.IsNullOrEmpty(duplicateGoogleMatches))
                                duplicateGoogleMatches = "Outlook contacts matching with multiple Google contacts have been found (either same email, Mobile or FullName) and cannot be synchronized. Please delete duplicates of:";

                            string str = olc.FileAs + " (" + olc.Email1Address + ", " + olc.MobileTelephoneNumber + ")";
                            if (!duplicateGoogleMatches.Contains(str))
                                duplicateGoogleMatches += Environment.NewLine + str;
                        }

                    }
                                        
                }                

                result.Add(match);
            }
            #endregion

            if (!string.IsNullOrEmpty(duplicateGoogleMatches))
                duplicatesFound = new DuplicateDataException(duplicateGoogleMatches);
            else
                duplicatesFound = null;

			//return result;

			//for each google contact that's left (they will be nonmatched) create a new match pair without outlook contact. 
			foreach (ContactEntry entry in sync.GoogleContacts)
			{
               
                    
				// only match if there is either an email or mobile phone or a name else
				// a matching google contact will be created at each sync
				if (entry.Emails.Count != 0 || entry.Phonenumbers.Count != 0 || !string.IsNullOrEmpty(entry.Title.Text))
				{                       
					ContactMatch match = new ContactMatch(null, entry); ;
					result.Add(match);
				}
				else
				{
					// no telephone and email
				}
			}
			return result;
		}

		private static bool IsContactValid(Outlook.ContactItem contact)
		{
			/*if (!string.IsNullOrEmpty(contact.FileAs))
				return true;*/

			if (!string.IsNullOrEmpty(contact.Email1Address))
				return true;

			if (!string.IsNullOrEmpty(contact.Email2Address))
				return true;

			if (!string.IsNullOrEmpty(contact.Email3Address))
				return true;

			if (!string.IsNullOrEmpty(contact.HomeTelephoneNumber))
				return true;

			if (!string.IsNullOrEmpty(contact.BusinessTelephoneNumber))
				return true;

			if (!string.IsNullOrEmpty(contact.MobileTelephoneNumber))
				return true;

			if (!string.IsNullOrEmpty(contact.HomeAddress))
				return true;

			if (!string.IsNullOrEmpty(contact.BusinessAddress))
				return true;

			if (!string.IsNullOrEmpty(contact.OtherAddress))
				return true;

			if (!string.IsNullOrEmpty(contact.Body))
				return true;

            if (contact.Birthday != DateTime.MinValue)
                return true;

			return false;
		}

		public static void SyncContacts(Syncronizer sync)
		{
			foreach (ContactMatch match in sync.Contacts)
			{
				SyncContact(match, sync);
			}
		}
		public static void SyncContact(ContactMatch match, Syncronizer sync)
		{
            if (match.GoogleContact == null && match.OutlookContact != null)
            {
                //no google contact               

                //TODO: found that when a contacts doesn't have anything other that the name - it's not returned in the google contacts list.
                Outlook.UserProperty idProp = match.OutlookContact.UserProperties[sync.OutlookPropertyNameId];
                if (idProp != null && (string)idProp.Value != "")
                {
                    AtomId id = new AtomId((string)idProp.Value);
                    ContactEntry matchingGoogleContact = sync.GoogleContacts.FindById(id) as ContactEntry;
                    if (matchingGoogleContact == null)
                    {
                        //TODO: make sure that outlook contacts don't get deleted when deleting corresponding google contact when testing. 
                        //solution: use ResetMatching() method to unlink this relation
                        //sync.ResetMatches();
                        return;
                    }
                }

                if (sync.SyncOption == SyncOption.GoogleToOutlookOnly)
                {
                    sync.SkippedCount++;
                    Logger.Log(string.Format("Outlook Contact not added to Google, because of SyncOption " + sync.SyncOption.ToString() + ": {0}", match.OutlookContact.FileAs), EventType.Information);
                    return;
                }

                //create a Google contact from Outlook contact
                match.GoogleContact = new ContactEntry();

                ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);

            }
            else if (match.OutlookContact == null && match.GoogleContact != null)
            {
                

                string outlookId = ContactPropertiesUtils.GetGoogleOutlookContactId(sync.SyncProfile, match.GoogleContact);
                if (outlookId != null)
                {
                    //TODO: make sure that google contacts don't get deleted when deleting corresponding outlook contact when testing. 
                    //solution: use ResetMatching() method to unlink this relation
                    //sync.ResetMatches();                    
                    return;

                }

                // no outlook contact
                if (sync.SyncOption == SyncOption.OutlookToGoogleOnly)
                {
                    sync.SkippedCount++;
                    Logger.Log(string.Format("Google Contact not added to Outlook, because of SyncOption " + sync.SyncOption.ToString() + ": {0}", match.GoogleContact.Title.Text), EventType.Information);
                    return;
                }

                //create a Outlook contact from Google contact
                match.OutlookContact = sync.OutlookApplication.CreateItem(Outlook.OlItemType.olContactItem) as Outlook.ContactItem;               

                ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);
            }
            else if (match.OutlookContact != null && match.GoogleContact != null)
            {
                //merge contact details


                //TODO: check if there are multiple matches
                //if (match.AllGoogleContactMatches.Count > 1)
                //{
                //    //loop from 2-nd item
                //    for (int m = 1; m < match.AllGoogleContactMatches.Count; m++)
                //    {
                //        ContactEntry entry = match.AllGoogleContactMatches[m];
                //        try
                //        {
                //            Outlook.ContactItem item = sync.OutlookContacts.Find("[" + sync.OutlookPropertyNameId + "] = \"" + entry.Id.Uri.Content + "\"") as Outlook.ContactItem;
                //            //Outlook.ContactItem item = sync.OutlookContacts.Find("[myTest] = \"value\"") as Outlook.ContactItem;
                //            if (item != null)
                //            {
                //                //do something
                //            }
                //        }
                //        catch (Exception)
                //        {
                //            //TODO: should not get here.
                //        }
                //    }

                //    //TODO: add info to Outlook contact from extra Google contacts before deleting extra Google contacts.

                //    for (int m = 1; m < match.AllGoogleContactMatches.Count; m++)
                //    {
                //        match.AllGoogleContactMatches[m].Delete();
                //    }
                //}

                //determine if this contact pair were syncronized
                //DateTime? lastUpdated = GetOutlookPropertyValueDateTime(match.OutlookContact, sync.OutlookPropertyNameUpdated);
                DateTime? lastSynced = GetOutlookPropertyValueDateTime(match.OutlookContact, sync.OutlookPropertyNameSynced);
                if (lastSynced.HasValue)
                {
                    //contact pair was syncronysed before.

                    //determine if google contact was updated since last sync

                    //lastSynced is stored without seconds. take that into account.
                    DateTime lastUpdatedOutlook = match.OutlookContact.LastModificationTime.AddSeconds(-match.OutlookContact.LastModificationTime.Second);
                    DateTime lastUpdatedGoogle = match.GoogleContact.Updated.AddSeconds(-match.GoogleContact.Updated.Second);

                    //check if both outlok and google contacts where updated sync last sync
                    if (lastUpdatedOutlook.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance
                        && lastUpdatedGoogle.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance)
                    {
                        //both contacts were updated.
                        //options: 1) ignore 2) loose one based on SyncOption
                        //throw new Exception("Both contacts were updated!");

                        switch (sync.SyncOption)
                        {
                            case SyncOption.MergeOutlookWins:
                            case SyncOption.OutlookToGoogleOnly:
                                //overwrite google contact
                                Logger.Log("Outlook and Google contact have been updated, Outlook contact is overwriting Google because of SyncOption " + sync.SyncOption + ": " + match.OutlookContact.FileAs + ".", EventType.Information);
                                ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                                sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);
                                break;
                            case SyncOption.MergeGoogleWins:
                            case SyncOption.GoogleToOutlookOnly:
                                //overwrite outlook contact
                                Logger.Log("Outlook and Google contact have been updated, Google contact is overwriting Outlook because of SyncOption " + sync.SyncOption + ": " + match.OutlookContact.FileAs + ".", EventType.Information);
                                ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                                sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);
                                break;
                            case SyncOption.MergePrompt:
                                //promp for sync option
                                ConflictResolver r = new ConflictResolver();
                                ConflictResolution res = r.Resolve(match.OutlookContact, match.GoogleContact);
                                switch (res)
                                {
                                    case ConflictResolution.Skip:
                                        break;
                                    case ConflictResolution.Cancel:
                                        throw new ApplicationException("Canceled");
                                    case ConflictResolution.OutlookWins:
                                        //TODO: what about categories/groups?
                                        ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                                        sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);
                                        break;
                                    case ConflictResolution.GoogleWins:
                                        //TODO: what about categories/groups?
                                        ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                                        sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);
                                        break;
                                    default:
                                        break;
                                }
                                break;
                        }
                        return;
                    }
                    

                    //check if outlook contact was updated (with X second tolerance)
                    if (sync.SyncOption != SyncOption.GoogleToOutlookOnly &&
                        (lastUpdatedOutlook.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance ||
                         lastUpdatedGoogle.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance &&
                         sync.SyncOption == SyncOption.OutlookToGoogleOnly
                        )
                       )
                    {
                        //outlook contact was changed or changed Google contact will be overwritten

                        if (lastUpdatedGoogle.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance && 
                            sync.SyncOption == SyncOption.OutlookToGoogleOnly)
                            Logger.Log("Google contact has been updated since last sync, but Outlook contact is overwriting Google because of SyncOption " + sync.SyncOption + ": " + match.OutlookContact.FileAs + ".", EventType.Information);

                        ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                        sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);

                        //at the moment use outlook as "master" source of contacts - in the event of a conflict google contact will be overwritten.
                        //TODO: control conflict resolution by SyncOption
                        return;                        
                    }

                    //check if google contact was updated (with X second tolerance)
                    if (sync.SyncOption != SyncOption.OutlookToGoogleOnly &&
                        (lastUpdatedGoogle.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance ||
                         lastUpdatedOutlook.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance &&
                         sync.SyncOption == SyncOption.GoogleToOutlookOnly
                        )
                       )
                    {
                        //google contact was changed or changed Outlook contact will be overwritten

                        if (lastUpdatedOutlook.Subtract(lastSynced.Value).TotalSeconds >= TimeTolerance &&
                            sync.SyncOption == SyncOption.GoogleToOutlookOnly)
                            Logger.Log("Outlook contact has been updated since last sync, but Google contact is overwriting Outlook because of SyncOption " + sync.SyncOption + ": " + match.OutlookContact.FileAs + ".", EventType.Information);
                        
                        ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                        sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);                                            
                    }
                }
                else
                {
                    //contacts were never synced.
                    //merge contacts.
                    switch (sync.SyncOption)
                    {
                        case SyncOption.MergeOutlookWins:
                        case SyncOption.OutlookToGoogleOnly:
                            //overwrite google contact
                            ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                            sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);
                            break;
                        case SyncOption.MergeGoogleWins:
                        case SyncOption.GoogleToOutlookOnly:
                            //overwrite outlook contact
                            ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                            sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);
                            break;
                        case SyncOption.MergePrompt:
                            //promp for sync option
                            ConflictResolver r = new ConflictResolver();
                            ConflictResolution res = r.Resolve(match.OutlookContact, match.GoogleContact);
                            switch (res)
                            {
                                case ConflictResolution.Skip:
                                    break;
                                case ConflictResolution.Cancel:
                                    throw new ApplicationException("Canceled");
                                case ConflictResolution.OutlookWins:
                                    ContactSync.MergeContacts(match.OutlookContact, match.GoogleContact);
                                    sync.OverwriteContactGroups(match.OutlookContact, match.GoogleContact);
                                    break;
                                case ConflictResolution.GoogleWins:
                                    ContactSync.MergeContacts(match.GoogleContact, match.OutlookContact);
                                    sync.OverwriteContactGroups(match.GoogleContact, match.OutlookContact);
                                    break;
                                default:
                                    break;
                            }
                            break;                        
                    }
                }

            }
            else
                throw new ArgumentNullException("ContactMatch has all peers null.");
                
		}

		private static PhoneNumber FindPhone(string number, PhonenumberCollection phones)
		{
			if (string.IsNullOrEmpty(number))
				return null;

			foreach (PhoneNumber phone in phones)
			{
				if (phone.Value.Equals(number, StringComparison.InvariantCultureIgnoreCase))
				{
					return phone;
				}
			}

			return null;
		}

		private static EMail FindEmail(string address, EMailCollection emails)
		{
			if (string.IsNullOrEmpty(address))
				return null;

			foreach (EMail email in emails)
			{
				if (address.Equals(email.Address, StringComparison.InvariantCultureIgnoreCase))
				{
					return email;
				}
			}

			return null;
		}

		public static DateTime? GetOutlookPropertyValueDateTime(Outlook.ContactItem outlookContact, string propertyName)
		{
			Outlook.UserProperty prop = outlookContact.UserProperties[propertyName];
			if (prop != null)
				return (DateTime)prop.Value;
			return null;
		}
        


		/// <summary>
		/// Adds new Google Groups to the Google account.
		/// </summary>
		/// <param name="sync"></param>
		public static void SyncGroups(Syncronizer sync)
		{
			foreach (ContactMatch match in sync.Contacts)
			{
				if (match.OutlookContact != null && !string.IsNullOrEmpty(match.OutlookContact.Categories))
				{
					string[] cats = Utilities.GetOutlookGroups(match.OutlookContact);
					GroupEntry g;
					foreach (string cat in cats)
					{
						g = sync.GetGoogleGroupByName(cat);
						if (g == null)
						{
							// create group
							if (g == null)
							{
								g = sync.CreateGroup(cat);
								g = sync.SaveGoogleGroup(g);
								sync.GoogleGroups.Add(g);
							}
						}
					}
				}
			}
		}
	}

	internal class ContactMatchList : List<ContactMatch>
	{
		public ContactMatchList(int capacity) : base(capacity) { }
	}

	internal class ContactMatch
	{
		public Outlook.ContactItem OutlookContact;
		public ContactEntry GoogleContact;
		public readonly List<ContactEntry> AllGoogleContactMatches = new List<ContactEntry>(1);
		public ContactEntry LastGoogleContact;

		public ContactMatch(Outlook.ContactItem outlookContact, ContactEntry googleContact)
		{
			OutlookContact = outlookContact;
			GoogleContact = googleContact;
		}

		public void AddGoogleContact(ContactEntry googleContact)
		{
			if (googleContact == null)
				return;
			//throw new ArgumentNullException("googleContact must not be null.");

			if (GoogleContact == null)
				GoogleContact = googleContact;

			//this to avoid searching the entire collection. 
			//if last contact it what we are trying to add the we have already added it earlier
			if (LastGoogleContact == googleContact)
				return;

			if (!AllGoogleContactMatches.Contains(googleContact))
				AllGoogleContactMatches.Add(googleContact);

			LastGoogleContact = googleContact;
		}

		public void Delete()
		{
			if (GoogleContact != null)
				GoogleContact.Delete();
			if (OutlookContact != null)
				OutlookContact.Delete();
		}
	}

	//public class GroupMatchList : List<GroupMatch>
	//{
	//    public GroupMatchList(int capacity) : base(capacity) { }
	//}

	//public class GroupMatch
	//{
	//    public string OutlookGroup;
	//    public GroupEntry GoogleGroup;
	//    public readonly List<GroupEntry> AllGoogleGroupMatches = new List<GroupEntry>(1);
	//    public GroupEntry LastGoogleGroup;

	//    public GroupMatch(string outlookGroup, GroupEntry googleGroup)
	//    {
	//        OutlookGroup = outlookGroup;
	//        GoogleGroup = googleGroup;
	//    }

	//    public void AddGoogleGroup(GroupEntry googleGroup)
	//    {
	//        if (googleGroup == null)
	//            return;
	//        //throw new ArgumentNullException("googleContact must not be null.");

	//        if (GoogleGroup == null)
	//            GoogleGroup = googleGroup;

	//        //this to avoid searching the entire collection. 
	//        //if last contact it what we are trying to add the we have already added it earlier
	//        if (LastGoogleGroup == googleGroup)
	//            return;

	//        if (!AllGoogleGroupMatches.Contains(googleGroup))
	//            AllGoogleGroupMatches.Add(googleGroup);

	//        LastGoogleGroup = googleGroup;
	//    }
	//}
}
