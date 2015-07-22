/*
 * Copyright (c) 2015 Gloebit LLC
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Web;
using log4net;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

using OpenSim.Framework;

namespace Gloebit.GloebitMoneyModule {

    public class GloebitAPI {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public readonly string m_key;
        private string m_keyAlias;
        private string m_secret;
        public readonly Uri m_url;
        
        public interface IAsyncEndpointCallback {
            void transactU2UCompleted (OSDMap responseDataMap, User sender, User recipient, Asset asset);
            void createSubscriptionCompleted(OSDMap responseDataMap, Subscription subscription);
            void createSubscriptionAuthorizationCompleted(OSDMap responseDataMap, Subscription subscription, User sender, IClientAPI client);
        }
        
        public static IAsyncEndpointCallback m_asyncEndpointCallbacks;

        public interface IAssetCallback {
            bool processAssetEnactHold(Asset asset, out string returnMsg);
            bool processAssetConsumeHold(Asset asset, out string returnMsg);
            bool processAssetCancelHold(Asset asset, out string returnMsg);
        }
        
        public static IAssetCallback m_assetCallbacks;

        public class User {
            public string PrincipalID;
            public string GloebitID;
            public string GloebitToken;

            // TODO - update tokenMap to be a proper LRU Cache and hold User objects
            private static Dictionary<string,string> s_tokenMap = new Dictionary<string, string>();

            public User() {
            }

            private User(string principalID, string gloebitID, string token) {
                this.PrincipalID = principalID;
                this.GloebitID = gloebitID;
                this.GloebitToken = token;
            }

            public static User Get(UUID agentID) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in User.Get");
                string agentIdStr = agentID.ToString();
                string token;
                lock(s_tokenMap) {
                    s_tokenMap.TryGetValue(agentIdStr, out token);
                }

                if(token == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior token for {0}", agentIdStr);
                    User[] users = GloebitUserData.Instance.Get("PrincipalID", agentIdStr);

                    switch(users.Length) {
                        case 1:
                            User u = users[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND USER TOKEN! {0} valid token? {1}", u.PrincipalID, !String.IsNullOrEmpty(u.GloebitToken));
                            return u;
                        case 0:
                            return new User(agentIdStr, String.Empty, token);
                        default:
                           throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one prior token for {0}", agentIdStr));
                    }
                    // TODO - use the Gloebit identity service for userId
                }

                //return null;
                return new User (agentIdStr, String.Empty, token);
            }

            public static User Init(UUID agentId, string token) {
                string agentIdStr = agentId.ToString();
                lock(s_tokenMap) {
                    s_tokenMap[agentIdStr] = token;
                }

                // TODO: properly store GloebitID when we get it.
                //User u = new User(agentIdStr, UUID.Zero.ToString(), token);
                User u = new User(agentIdStr, String.Empty, token);
                GloebitUserData.Instance.Store(u);
                return u;
            }

            public void InvalidateToken() {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.User.InvalidateToken() {0}, valid token? {1}", PrincipalID, !String.IsNullOrEmpty(GloebitToken)); 
                if(!String.IsNullOrEmpty(GloebitToken)) {
                    GloebitToken = String.Empty;
                    lock(s_tokenMap) {
                        s_tokenMap.Remove(PrincipalID);
                    }
                    GloebitUserData.Instance.Store(this);
                }
            }
        }
        
        // TODO: turn into class Transaction - consider if we should have a separate "asset" class for callbacks and delivery.
        public class Asset {
            // Primary Key value
            public UUID TransactionID;
            
            // Data required when enacting/consume/canceling this asset or for additional info
            public UUID BuyerID;
            public UUID SellerID;
            
            public bool GhostAsset;     // Set to true when asset is used for callback notification, but has no object to deliver
            public UUID PartID;         // UUID of object
            public string PartName;     // object name
            public UUID CategoryID;     // Appears to be a folder id used when saleType is copy
            public uint LocalID;        // Region specific ID of object.  Unclear why this is passed instead of UUID
            public int SaleType;        // object, copy, or contents
            public int SalePrice;
            public int BuyerEndingBalance;  // balance returned by transact when fully successful.
            
            // State variables used internally in GloebitAPI
            public bool enacted;
            public bool consumed;
            public bool canceled;
            
            // Timestamps for reporting
            public DateTime cTime;
            public DateTime? enactedTime;
            public DateTime? finishedTime;
            
            private static Dictionary<string, Asset> s_assetMap = new Dictionary<string, Asset>();
            private static Dictionary<string, Asset> s_pendingAssetMap = new Dictionary<string, Asset>(); // tracks assets currently being worked on so that two state functions are not enacted at the same time.
            
            // Necessary for use with standard db serialization system
            public Asset() {
            }
            
            private Asset(UUID transactionID, UUID buyerID, UUID sellerID, bool ghostAsset, UUID partID, string partName, UUID categoryID, uint localID, int saleType, int salePrice) {
                // Primary Key value
                this.TransactionID = transactionID;
                
                // Data required when enacting/consume/canceling this asset or for additional info
                this.GhostAsset = ghostAsset;
                this.BuyerID = buyerID;
                this.SellerID = sellerID;
                this.PartID = partID;
                this.PartName = partName;
                this.CategoryID = categoryID;
                this.LocalID = localID;
                this.SaleType = saleType;
                this.SalePrice = salePrice;
                this.BuyerEndingBalance = -1;
                
                // State variables used internally in GloebitAPI
                this.enacted = false;
                this.consumed = false;
                this.canceled = false;
                
                // Timestamps for reporting
                this.cTime = DateTime.UtcNow;
                this.enactedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
                this.finishedTime = null; // set to null instead of DateTime.MinValue to avoid crash on reading 0 timestamp
                // TODO: We have made these nullable and initialize to null.  We could alternatively choose a time that is not zero
                // and avoid any potential conficts from allowing null.
                // On MySql, I had to set the columns to allow NULL, otherwise, inserting null defaulted to the current local time.
                // On PGSql, I set the columns to allow NULL, but haven't tested.
                // On SQLite, I don't think that you can set them to allow NULL explicitely, and haven't checked defaults.
            }
            
            public static Asset Get(UUID transactionID) {
                return Get(transactionID.ToString());
            }
            
            public static Asset Get(string transactionIDStr) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Asset.Get");
                Asset asset = null;
                lock(s_assetMap) {
                    s_assetMap.TryGetValue(transactionIDStr, out asset);
                }
                
                if(asset == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior asset for {0}", transactionIDStr);
                    Asset[] assets = GloebitAssetData.Instance.Get("TransactionID", transactionIDStr);
                    
                    switch(assets.Length) {
                        case 1:
                            asset = assets[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND ASSET! {0} {1} {2}", asset.TransactionID, asset.BuyerID, asset.SellerID);
                            lock(s_assetMap) {
                                s_assetMap[transactionIDStr] = asset;
                            }
                            return asset;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find asset matching tID:{0}", asset.TransactionID);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one asset for {0}", transactionIDStr));
                            return null;
                    }
                }
                
                return asset;
            }
            
            public static Asset Init(UUID transactionID, UUID buyerID, UUID sellerID, bool ghostAsset, UUID partID, string partName, UUID categoryID, uint localID, int saleType, int salePrice) {
                string transactionIDstr = transactionID.ToString();
                // string buyerIDstr = buyerID.ToString();
                // string sellerIDstr = sellerID.ToString();
                
                Asset a = new Asset(transactionID, buyerID, sellerID, ghostAsset, partID, partName, categoryID, localID, saleType, salePrice);
                lock(s_assetMap) {
                    s_assetMap[transactionIDstr] = a;
                }
                
                GloebitAssetData.Instance.Store(a);
                return a;
            }
            
            public Uri BuildEnactURI(Uri baseURI) {
                UriBuilder enact_uri = new UriBuilder(baseURI);
                enact_uri.Path = "gloebit/asset";
                enact_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "enact");
                return enact_uri.Uri;
            }
            public Uri BuildConsumeURI(Uri baseURI) {
                UriBuilder consume_uri = new UriBuilder(baseURI);
                consume_uri.Path = "gloebit/asset";
                consume_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "consume");
                return consume_uri.Uri;
            }
            public Uri BuildCancelURI(Uri baseURI) {
                UriBuilder cancel_uri = new UriBuilder(baseURI);
                cancel_uri.Path = "gloebit/asset";
                cancel_uri.Query = String.Format("id={0}&state={1}", this.TransactionID, "cancel");
                return cancel_uri.Uri;
            }
            
            /**************************************************/
            /******* ASSET STATE MACHINE **********************/
            /**************************************************/
            
            public static bool ProcessStateRequest(string transactionIDstr, string stateRequested, out string returnMsg) {
                bool result = false;
                
                // Retrieve asset
                Asset myAsset = Asset.Get(UUID.Parse(transactionIDstr));
                
                // If no matching asset, return false
                // TODO: is this what we want to return?
                if (myAsset == null) {
                    returnMsg = "No matching asset found.";
                    return false;
                }
                
                // Attempt to avoid race conditions (not sure if even possible)
                bool alreadyProcessing = false;
                lock(s_pendingAssetMap) {
                    alreadyProcessing = s_pendingAssetMap.ContainsKey(transactionIDstr);
                    if (!alreadyProcessing) {
                        // add to race condition protection
                        s_pendingAssetMap[transactionIDstr] = myAsset;
                    }
                }
                if (alreadyProcessing) {
                    returnMsg = "pending";  // DO NOT CHANGE --- this message needs to be returned to Gloebit to know it is a retryable error
                    return false;
                }
                
                // Call proper state processor
                switch (stateRequested) {
                    case "enact":
                        result = myAsset.enactHold(out returnMsg);
                        break;
                    case "consume":
                        result = myAsset.consumeHold(out returnMsg);
                        if (result) {
                            lock(s_assetMap) {
                                s_assetMap.Remove(transactionIDstr);
                            }
                        }
                        break;
                    case "cancel":
                        result = myAsset.cancelHold(out returnMsg);
                        if (result) {
                            lock(s_assetMap) {
                                s_assetMap.Remove(transactionIDstr);
                            }
                        }
                        break;
                    default:
                        // no recognized state request
                        returnMsg = "Unrecognized state request";
                        result = false;
                        break;
                }
                
                // remove from race condition protection
                lock(s_pendingAssetMap) {
                    s_pendingAssetMap.Remove(transactionIDstr);
                }
                return result;
            }
            
            private bool enactHold(out string returnMsg) {
                if (this.canceled) {
                    // getting a delayed enact sent before cancel.  return false.
                    returnMsg = "Enact: already canceled";
                    return false;
                }
                if (this.consumed) {
                    // getting a delayed enact sent before consume.  return true.
                    returnMsg = "Enact: already consumed";
                    return true;
                }
                if (this.enacted) {
                    // already enacted. return true.
                    returnMsg = "Enact: already enacted";
                    return true;
                }
                // First reception of enact for asset.  Do specific enact functionality
                this.enacted = m_assetCallbacks.processAssetEnactHold(this, out returnMsg); // Do I need to grab the money module for this?
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.enactHold: {0}", this.enacted);
                if (this.enacted) {
                    m_log.InfoFormat("TransactionID: {0}", this.TransactionID);
                    m_log.InfoFormat("GhostAsset: {0}", this.GhostAsset);
                    m_log.InfoFormat("BuyerID: {0}", this.BuyerID);
                    m_log.InfoFormat("SellerID: {0}", this.SellerID);
                    m_log.InfoFormat("PartID: {0}", this.PartID);
                    m_log.InfoFormat("PartName: {0}", this.PartName);
                    m_log.InfoFormat("CategoryID: {0}", this.CategoryID);
                    m_log.InfoFormat("LocalID: {0}", this.LocalID);
                    m_log.InfoFormat("SaleType: {0}", this.SaleType);
                    m_log.InfoFormat("SalePrice: {0}", this.SalePrice);
                    m_log.InfoFormat("BuyerEndingBalance: {0}", this.BuyerEndingBalance);
                    m_log.InfoFormat("enacted: {0}", this.enacted);
                    m_log.InfoFormat("consumed: {0}", this.consumed);
                    m_log.InfoFormat("canceled: {0}", this.canceled);
                    m_log.InfoFormat("cTime: {0}", this.cTime);
                    m_log.InfoFormat("enactedTime: {0}", this.enactedTime);
                    m_log.InfoFormat("finishedTime: {0}", this.finishedTime);

                    // TODO: Should we store and update the time even if it fails to track time enact attempted/failed?
                    this.enactedTime = DateTime.UtcNow;
                    GloebitAssetData.Instance.Store(this);
                }
                return this.enacted;
            }
            
            private bool consumeHold(out string returnMsg) {
                if (this.canceled) {
                    // Should never get a delayed consume after a cancel.  return false.
                    returnMsg = "Consume: already canceled";
                    return false;
                }
                if (!this.enacted) {
                    // Should never get a consume before we've enacted.  return false.
                    returnMsg = "Consume: Not yet enacted";
                    return false;
                }
                if (this.consumed) {
                    // already consumed. return true.
                    returnMsg = "Cosume: Already consumed";
                    return true;
                }
                // First reception of consume for asset.  Do specific consume functionality
                this.consumed = m_assetCallbacks.processAssetConsumeHold(this, out returnMsg); // Do I need to grab the money module for this?
                if (this.consumed) {
                    this.finishedTime = DateTime.UtcNow;
                    GloebitAssetData.Instance.Store(this);
                }
                return this.consumed;
            }
            
            private bool cancelHold(out string returnMsg) {
                if (this.consumed) {
                    // Should never get a delayed cancel after a consume.  return false.
                    returnMsg = "Cancel: already consumed";
                    return false;
                }
                if (!this.enacted) {
                    // Hasn't enacted.  No work to undo.  return true.
                    returnMsg = "Cancel: not yet enacted";
                    // don't return here.  Still want to process cancel which will need to assess if enacted.
                    //return true;
                }
                if (this.canceled) {
                    // already canceled. return true.
                    returnMsg = "Cancel: already canceled";
                    return true;
                }
                // First reception of cancel for asset.  Do specific cancel functionality
                this.canceled = m_assetCallbacks.processAssetCancelHold(this, out returnMsg); // Do I need to grab the money module for this?
                if (this.canceled) {
                    this.finishedTime = DateTime.UtcNow;
                    GloebitAssetData.Instance.Store(this);
                }
                return this.canceled;
            }
        }
        
        public class Subscription {
            
            // These 3 make up the primary key -- allows sim to swap back and forth between apps or GlbEnvs without getting errors
            public UUID ObjectID;       // ID of object with an LLGiveMoney or LLTransferLinden's script - local subscription ID
            public string AppKey;       // AppKey active when created
            public string GlbApiUrl;    // GlbEnv Url active when created
            
            public UUID SubscriptionID; // ID returned by create-subscription Gloebit endpoint
            public bool Enabled;        // enabled returned by Gloebit Endpoint - if not enabled, can't use.
            public DateTime ctime;      // time of creation
            
            // TODO: Are these necessary beyond sending to Gloebit? - can be rebuilt from object
            // TODO: a name or description change doesn't necessarily change the the UUID of the object --- how to deal with this?
            // TODO: name and description could be empty/blank --
            public string ObjectName;   // Name of object - treated as subscription_name by Gloebit
            public string Description;  // subscription_description - (should include object descriptin, but may include additional details)
            // TODO: additional details --- how to store --- do we need to store?
            
            private static Dictionary<string, Subscription> s_subscriptionMap = new Dictionary<string, Subscription>();
            
            public Subscription() {
            }
            
            private Subscription(UUID objectID, string appKey, string apiURL, string objectName, string objectDescription) {
                this.ObjectID = objectID;
                this.AppKey = appKey;
                this.GlbApiUrl = apiURL;
                
                this.ObjectName = objectName;
                this.Description = objectDescription;
                
                // Set defaults until we fill them in
                SubscriptionID = UUID.Zero;
                this.ctime = DateTime.UtcNow;
                this.Enabled = false;
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription() oID:{0}, oN:{1}, oD:{2}", ObjectID, ObjectName, Description);
                
            }
            
            public static Subscription Init(UUID objectID, string appKey, string apiUrl, string objectName, string objectDescription) {
                string objectIDstr = objectID.ToString();
                
                Subscription s = new Subscription(objectID, appKey, apiUrl, objectName, objectDescription);
                lock(s_subscriptionMap) {
                    s_subscriptionMap[objectIDstr] = s;
                }
                
                GloebitSubscriptionData.Instance.Store(s);
                return s;
            }
            
            public static Subscription[] Get(UUID objectID) {
                return Get(objectID.ToString());
            }
            
            public static Subscription[] Get(string objectIDStr) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.Get");
                Subscription subscription = null;
                lock(s_subscriptionMap) {
                    s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
                }
                
                /*if(subscription == null) {*/
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for subscriptions for {0}", objectIDStr);
                Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get("ObjectID", objectIDStr);
                    /*
                    Subscription[] subsForAppWithKey = new Subscription[];
                    foreach (Subscription sub in subscriptions) {
                        if (sub.AppKey = "appkey" && sub.GlbApiUrl = "url") {
                            subsForAppWithKey.Append(sub);
                        }
                    }
                     */
                bool cacheDuplicate = false;
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Found {0} subscriptions for {0} saved in the DB", subscriptions.Length, objectIDStr);
                if (subscription != null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Found 1 cached subscriptions for {0}", subscriptions.Length, objectIDStr);
                    if (subscriptions.Length == 0) {
                        subscriptions = new Subscription[1];
                        subscriptions[0] = subscription;
                    } else {
                        for (int i = 0; i < subscriptions.Length; i++) {
                            if (subscriptions[i].ObjectID == subscription.ObjectID &&
                                subscriptions[i].AppKey == subscription.AppKey &&
                                subscriptions[i].GlbApiUrl == subscription.GlbApiUrl)
                            {
                                cacheDuplicate = true;
                                subscriptions[i] = subscription;
                                m_log.InfoFormat("[GLOEBITMONEYMODULE] Cached subscription was in db.  Replacing with cached version.");
                                break;
                            }
                        }
                        if (!cacheDuplicate) {
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Combining Cached subscription with those from db.");
                            Subscription[] dbSubs = subscriptions;
                            subscriptions = new Subscription[dbSubs.Length + 1];
                            subscriptions[0] = subscription;
                            for (int i = 1; i < subscriptions.Length; i++) {
                                subscriptions[i] = dbSubs[i-1];
                            }
                        }
                        
                    }
                    
                } else {
                     m_log.InfoFormat("[GLOEBITMONEYMODULE] Found no cached subscriptions for {0}", subscriptions.Length, objectIDStr);
                }
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Returning {0} subscriptions for {0}", subscriptions.Length, objectIDStr);
                return subscriptions;
                 /*
                    switch(subscriptions.Length) {
                        case 1:
                            subscription = subsForAppWithKey[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION! {0} {1} {2} {3}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID);
                            lock(s_subscriptionMap) {
                                s_subscriptionMap[objectIDStr] = subscription;
                            }
                            return subscription;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0}", objectIDStr);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr));
                            return null;
                    }
                }
                
                return subscription;
                  */
            }
            public static Subscription Get(UUID objectID, string appKey, Uri apiUrl) {
                return Get(objectID.ToString(), appKey, apiUrl.ToString());
            }
            
            public static Subscription Get(string objectIDStr, string appKey, Uri apiUrl) {
                return Get(objectIDStr, appKey, apiUrl.ToString());
            }
            
            public static Subscription Get(UUID objectID, string appKey, string apiUrl) {
                return Get(objectID.ToString(), appKey, apiUrl);
            }
            
            public static Subscription Get(string objectIDStr, string appKey, string apiUrl) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.Get");
                Subscription subscription = null;
                lock(s_subscriptionMap) {
                    s_subscriptionMap.TryGetValue(objectIDStr, out subscription);
                }
                
                if(subscription == null) {
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl);
                    string[] keys = new string[] {"ObjectID", "AppKey", "GlbApiUrl"};
                    string[] values = new string[] {objectIDStr, appKey, apiUrl};
                    Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);
                    
                    switch(subscriptions.Length) {
                        case 1:
                            subscription = subscriptions[0];
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                            lock(s_subscriptionMap) {
                                s_subscriptionMap[objectIDStr] = subscription;
                            }
                            return subscription;
                        case 0:
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching oID:{0} appKey:{1} apiUrl:{2}", objectIDStr, appKey, apiUrl);
                            return null;
                        default:
                            throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1} {2}", objectIDStr, appKey, apiUrl));
                            return null;
                    }
                }
                m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                return subscription;
            }
            
            public static Subscription GetBySubscriptionID(string subscriptionIDStr, string apiUrl) {
                m_log.InfoFormat("[GLOEBITMONEYMODULE] in Subscription.GetBySubscriptionID");
                Subscription subscription = null;
                Subscription localSub = null;
                
                
                m_log.InfoFormat("[GLOEBITMONEYMODULE] Looking for prior subscription for {0} {1}", subscriptionIDStr, apiUrl);
                string[] keys = new string[] {"SubscriptionID", "GlbApiUrl"};
                string[] values = new string[] {subscriptionIDStr, apiUrl};
                Subscription[] subscriptions = GloebitSubscriptionData.Instance.Get(keys, values);
                
                    
                switch(subscriptions.Length) {
                    case 1:
                        subscription = subscriptions[0];
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in DB! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                        lock(s_subscriptionMap) {
                            s_subscriptionMap.TryGetValue(subscription.ObjectID.ToString(), out localSub);
                            if (localSub == null) {
                                s_subscriptionMap[subscription.ObjectID.ToString()] = subscription;
                            }
                        }
                        if (localSub == null) {
                            // do nothing.  already added subscription to cache in lock
                        } else if (localSub.Equals(subscription)) {
                            // return cached sub instead of new sub from DB
                            subscription = localSub;
                        } else {
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] mapped Subscription is not equal to DB return --- shouldn't happen.  Investigate.");
                            m_log.ErrorFormat("Local Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", localSub.SubscriptionID, localSub.ObjectID, localSub.AppKey, localSub.GlbApiUrl, localSub.ObjectName, localSub.Description, localSub.Enabled, localSub.ctime);
                            m_log.ErrorFormat("DB Sub\n sID:{0}\n oID:{1}\n appKey:{2}\n apiUrl:{3}\n oN:{4}\n oD:{5}\n enabled:{6}\n ctime:{7}", subscription.SubscriptionID, subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.ObjectName, subscription.Description, subscription.Enabled, subscription.ctime);
                            // still return cached sub instead of new sub from DB
                            subscription = localSub;
                        }
                        return subscription;
                    case 0:
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] Could not find subscription matching sID:{0} apiUrl:{1}", subscriptionIDStr, apiUrl);
                        return null;
                    default:
                        throw new Exception(String.Format("[GLOEBITMONEYMODULE] Failed to find exactly one subscription for {0} {1}", subscriptionIDStr, apiUrl));
                        return null;
                }
                m_log.InfoFormat("[GLOEBITMONEYMODULE] FOUND SUBSCRIPTION in cache! oID:{0} appKey:{1} url:{2} sID:{3} oN:{4} oD:{5}", subscription.ObjectID, subscription.AppKey, subscription.GlbApiUrl, subscription.SubscriptionID, subscription.ObjectName, subscription.Description);
                return subscription;
            }
            
            public override bool Equals(Object obj)
            {
                //Check for null and compare run-time types.
                if ((obj == null) || ! this.GetType().Equals(obj.GetType())) {
                    return false;
                }
                else {
                    Subscription s = (Subscription) obj;
                    // TODO: remove these info logs once we understand why things are not always equal
                    // m_log.InfoFormat("[GLOEBITMONEYMODULE] Subscription.Equals()");
                    // m_log.InfoFormat("ObjectID:{0}", (ObjectID == s.ObjectID));
                    // m_log.InfoFormat("AppKey:{0}", (AppKey == s.AppKey));
                    // m_log.InfoFormat("GlbApiUrl:{0}", (GlbApiUrl == s.GlbApiUrl));
                    // m_log.InfoFormat("ObjectName:{0}", (ObjectName == s.ObjectName));
                    // m_log.InfoFormat("Description:{0}", (Description == s.Description));
                    // m_log.InfoFormat("SubscriptionID:{0}", (SubscriptionID == s.SubscriptionID));
                    // m_log.InfoFormat("Enabled:{0}", (Enabled == s.Enabled));
                    // m_log.InfoFormat("ctime:{0}", (ctime == s.ctime));
                    // m_log.InfoFormat("ctime Equals:{0}", (ctime.Equals(s.ctime)));
                    // m_log.InfoFormat("ctime CompareTo:{0}", (ctime.CompareTo(s.ctime)));
                    // m_log.InfoFormat("ctime ticks:{0} == {1}", ctime.Ticks, s.ctime.Ticks);
                    
                    // NOTE: intentionally does not compare ctime as db truncates miliseconds to zero.
                    return ((ObjectID == s.ObjectID) &&
                            (AppKey == s.AppKey) &&
                            (GlbApiUrl == s.GlbApiUrl) &&
                            (ObjectName == s.ObjectName) &&
                            (Description == s.Description) &&
                            (SubscriptionID == s.SubscriptionID) &&
                            (Enabled == s.Enabled));
                }
            }
            

        }
        
        private delegate void CompletionCallback(OSDMap responseDataMap);

        private class GloebitRequestState {
            
            // Web request variables
            public HttpWebRequest request;
            public Stream responseStream;
            
            // Variables for storing Gloebit response stream data asynchronously
            public const int BUFFER_SIZE = 1024;    // size of buffer for max individual stream read events
            public byte[] bufferRead;               // buffer read to by stream read events
            public Decoder streamDecoder;           // Decoder for converting buffer to string in parts
            public StringBuilder responseData;      // individual buffer reads compiled/appended to full data

            public CompletionCallback continuation;
            
            // TODO: What to do when error states are reached since there is no longer a return?  Should we store an error state in a member variable?
            
            // Preferred constructor - use if we know the endpoint and agentID at creation time.
            public GloebitRequestState(HttpWebRequest req, CompletionCallback continuation)
            {
                request = req;
                responseStream = null;
                
                bufferRead = new byte[BUFFER_SIZE];
                streamDecoder = Encoding.UTF8.GetDecoder();     // Create Decoder for appropriate enconding type.
                responseData = new StringBuilder(String.Empty);

                this.continuation = continuation;
            }
            
        }

        public GloebitAPI(string key, string keyAlias, string secret, Uri url, IAsyncEndpointCallback asyncEndpointCallbacks, IAssetCallback assetCallbacks) {
            m_key = key;
            m_keyAlias = keyAlias;
            m_secret = secret;
            m_url = url;
            m_asyncEndpointCallbacks = asyncEndpointCallbacks;
            m_assetCallbacks = assetCallbacks;
        }
        
        /************************************************/
        /******** OAUTH2 AUTHORIZATION FUNCTIONS ********/
        /************************************************/


        /// <summary>
        /// Helper function to build the auth redirect callback url consistently everywhere.
        /// <param name="baseURI">The base url where this server's http services can be accessed.</param>
        /// <param name="agentId">The uuid of the agent being authorized.</param>
        /// </summary>
        private static Uri BuildAuthCallbackURL(Uri baseURI, UUID agentId) {
            UriBuilder redirect_uri = new UriBuilder(baseURI);
            redirect_uri.Path = "gloebit/auth_complete";
            redirect_uri.Query = String.Format("agentId={0}", agentId);
            return redirect_uri.Uri;
        }

        /// <summary>
        /// Request Authorization for this grid/region to enact Gloebit functionality on behalf of the specified OpenSim user.
        /// Sends Authorize URL to user which will launch a Gloebit authorize dialog.  If the user launches the URL and approves authorization from a Gloebit account, an authorization code will be returned to the redirect_uri.
        /// This is how a user links a Gloebit account to this OpenSim account.
        /// </summary>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        public void Authorize(IClientAPI user, Uri baseURI) {

            //********* BUILD AUTHORIZE QUERY ARG STRING ***************//
            ////Dictionary<string, string> auth_params = new Dictionary<string, string>();
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            if(m_keyAlias != null && m_keyAlias != "") {
                auth_params["r"] = m_keyAlias;
            }

            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURI, user.AgentId).ToString();
            auth_params["response_type"] = "code";
            auth_params["user"] = user.AgentId.ToString();
            // TODO - make use of 'state' param for XSRF protection
            // auth_params["state"] = ???;

            string query_string = BuildURLEncodedParamString(auth_params);

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize query_string: {0}", query_string);

            //********** BUILD FULL AUTHORIZE REQUEST URI **************//

            Uri request_uri = new Uri(m_url, String.Format("oauth2/authorize?{0}", query_string));
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Authorize request_uri: {0}", request_uri);
            
            //*********** SEND AUTHORIZE REQUEST URI TO USER ***********//
            // currently can not launch browser directly for user, so send in message
            
            // TODO: Shouldn't this be an interface function from the GMM since launching a web page will be specific to the integration?
            SendUrlToClient(user, "AUTHORIZE GLOEBIT", "To use Gloebit currency, please authorize Gloebit to link to your avatar's account on this web page:", request_uri);

        }
        
        /// <summary>
        /// Begins request to exchange an authorization code granted from the Authorize endpoint for an access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.
        /// This begins the second phase of the OAuth2 process.  It is activated by the redirect_uri of the Authorize function.
        /// This occurs completely behind the scenes for security purposes.
        /// </summary>
        /// <returns>The authenticated User object containing the access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.</returns>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        /// <param name="auth_code">Authorization Code returned to the redirect_uri from the Gloebit Authorize endpoint.</param>
        public void ExchangeAccessToken(IClientAPI user, string auth_code, Uri baseURI) {
            
            //TODO stop logging auth_code
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.ExchangeAccessToken Name:[{0}] AgentID:{1} auth_code:{1}", user.Name, user.AgentId, auth_code);
            
            // ************ BUILD EXCHANGE ACCESS TOKEN POST REQUEST ******** //
            OSDMap auth_params = new OSDMap();

            auth_params["client_id"] = m_key;
            auth_params["client_secret"] = m_secret;
            auth_params["code"] = auth_code;
            auth_params["grant_type"] = "authorization_code";
            auth_params["scope"] = "user balance transact";
            auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURI, user.AgentId).ToString();
            
            HttpWebRequest request = BuildGloebitRequest("oauth2/access-token", "POST", null, "application/x-www-form-urlencoded", auth_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.oauth2/access-token failed to create HttpWebRequest");
                // TODO: signal error
                return;
            }
            
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
                new GloebitRequestState(request,
                    delegate(OSDMap responseDataMap) {
                        // ************ PARSE AND HANDLE EXCHANGE ACCESS TOKEN RESPONSE ********* //

                        string token = responseDataMap["access_token"];
                        // TODO - do something to handle the "refresh_token" field properly
                        if(!String.IsNullOrEmpty(token)) {
                            User u = User.Init(user.AgentId, token);
                            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken Success User:{0}", u);

                            // TODO - make this use a callback
                            user.SendMoneyBalance(UUID.Zero, true, new byte[0], (int)GetBalance(u), 0, UUID.Zero, false, UUID.Zero, false, 0, String.Empty);
                        } else {
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CompleteExchangeAccessToken error: {0}, reason: {1}", responseDataMap["error"], responseDataMap["reason"]);
                            // TODO: signal error;
                        }
                    }));
        }
        
        
        /***********************************************/
        /********* GLOEBIT FUNCTIONAL ENDPOINS *********/
        /***********************************************/
        
        // ******* GLOEBIT BALANCE ENDPOINTS ********* //
        // requires "balance" in scope of authorization token
        

        /// <summary>
        /// Requests the Gloebit balance for the OpenSim user with this OpenSim agentID.
        /// Returns zero if a link between this OpenSim user and a Gloebit account have not been created and the user has not granted authorization to this grid/region.
        /// Requires "balance" in scope of authorization token.
        /// </summary>
        /// <returns>The Gloebit balance for the Gloebit accunt the user has linked to this OpenSim agentID on this grid/region.  Returns zero if a link between this OpenSim user and a Gloebit account has not been created and the user has not granted authorization to this grid/region.</returns>
        /// <param name="user">User object for the OpenSim user for whom the balance request is being made. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        public double GetBalance(User user) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance for agentID:{0}", user.PrincipalID);
            
            //************ BUILD GET BALANCE GET REQUEST ********//
            
            HttpWebRequest request = BuildGloebitRequest("balance", "GET", user);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance failed to create HttpWebRequest");
                return 0;
            }
            
            //************ PARSE AND HANDLE GET BALANCE RESPONSE *********//
            
            HttpWebResponse response = (HttpWebResponse) request.GetResponse();
            string status = response.StatusDescription;
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance status:{0}", status);
            using(StreamReader response_stream = new StreamReader(response.GetResponseStream())) {
                string response_str = response_stream.ReadToEnd();

                OSDMap responseData = (OSDMap)OSDParser.DeserializeJson(response_str);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.balance responseData:{0}", responseData.ToString());

                if (responseData["success"]) {
                    double balance = responseData["balance"].AsReal();
                    return balance;
                } else {
                    string reason = responseData["reason"];
                    switch(reason) {
                        case "unknown token1":
                        case "unknown token2":
                            // The token is invalid (probably the user revoked our app through the website)
                            // so force a reauthorization next time.
                            user.InvalidateToken();
                            break;
                        default:
                            m_log.ErrorFormat("Unknown error getting balance, reason: '{0}'", reason);
                            break;
                    }
                    return 0.0;
                }
            }

        }
        
        // ******* GLOEBIT TRANSACT ENDPOINTS ********* //
        // requires "transact" in scope of authorization token

        /// <summary>
        /// Begins Gloebit transaction request for the gloebit amount specified from the sender to the owner of the Gloebit app this module is connected to.
        /// </summary>
        /// <param name="sender">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="senderName">OpenSim Name of the user on this grid sending the gloebits.</param>
        /// <param name="amount">quantity of gloebits to be transacted.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.</param>
        public void Transact(User sender, string senderName, int amount, string description) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.transact senderID:{0} senderName:{1} amount:{2} description:{3}", sender.PrincipalID, senderName, amount, description);
            
            UUID transactionId = UUID.Random();

            OSDMap transact_params = new OSDMap();

            transact_params["version"] = 1;
            transact_params["application-key"] = m_key;
            transact_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            transact_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);

            transact_params["transaction-id"] = transactionId.ToString();
            transact_params["gloebit-balance-change"] = amount;
            transact_params["asset-code"] = description;
            transact_params["asset-quantity"] = 1;
            
            HttpWebRequest request = BuildGloebitRequest("transact", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.transact failed to create HttpWebRequest");
                return;
                // TODO once we return, return error value
            }
            
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
                new GloebitRequestState(request, 
                    delegate(OSDMap responseDataMap) {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact response: {0}", responseDataMap);

                        //************ PARSE AND HANDLE TRANSACT RESPONSE *********//

                        bool success = (bool)responseDataMap["success"];
                        // TODO: if success=false: id, balance, product-count are invalid.  Do not set balance.
                        double balance = responseDataMap["balance"].AsReal();
                        string reason = responseDataMap["reason"];
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact success: {0} balance: {1} reason: {2}", success, balance, reason);
                        // TODO - update the user's balance
                    }));
        }
        

        // TODO: does recipient have to authorize app?  Do they need to become a merchant on that platform or opt in to agreeing to receive gloebits?  How do they currently authorize sale on a grid?
        // TODO: Should we pass a bool for charging a fee or the actual fee % -- to the module owner --- could always charge a fee.  could be % set in app for when charged.  could be % set for each transaction type in app.
        // TODO: Should we always charge our fee, or have a bool or transaction type for occasions when we may not charge?
        // TODO: Do we need an endpoint for reversals/refunds, or just an admin interface from Gloebit?
        
        /// <summary>
        /// Request Gloebit transaction for the gloebit amount specified from the sender to the recipient.
        /// </summary>
        /// <param name="senderID">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="senderName">OpenSim Name of the user on this grid sending the gloebits.</param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientName">OpenSim Name of the user on this grid receiving the gloebits.</param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="amount">quantity of gloebits to be transacted.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.</param>
        /// <param name="asset">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <param name="transactionId">UUID provided by calling application.  This ID will be provided back to the application in any callbacks and allows for Idempotence.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, see buildTransactionDescMap helper function.</param>
        /// <param name="baseURI">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <returns>true if async transactU2U web request was built and submitted successfully; false if failed to submit request;  If true, IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.</returns>

        public bool TransactU2U(User sender, string senderName, User recipient, string recipientName, string recipientEmail, int amount, string description, Asset asset, UUID transactionId, OSDMap descMap, Uri baseURI) {

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U senderID:{0} senderName:{1} recipientID:{2} recipientName:{3} recipientEmail:{4} amount:{5} description:{6} baseURI:{7}", sender.PrincipalID, senderName, recipient.PrincipalID, recipientName, recipientEmail, amount, description, baseURI);
            
            // ************ IDENTIFY GLOEBIT RECIPIENT ******** //
            // TODO: How do we identify recipient?  Get email from profile from OpenSim UUID?
            // TODO: If we use emails, we may need to make sure account merging works for email/3rd party providers.
            // TODO: If we allow anyone to receive, need to ensure that gloebits received are locked down until user authenticates as merchant.
            
            // ************ BUILD AND SEND TRANSACT U2U POST REQUEST ******** //
            
            // TODO: remove and always pass in UUID
            if (transactionId == UUID.Zero) {
                transactionId = UUID.Random();
            }
            
            OSDMap transact_params = new OSDMap();
            
            transact_params["version"] = 1;
            transact_params["application-key"] = m_key;
            transact_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            //transact_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);
            transact_params["username-on-application"] = senderName;
            
            transact_params["transaction-id"] = transactionId.ToString();
            transact_params["gloebit-balance-change"] = amount;
            transact_params["asset-code"] = description;
            transact_params["asset-quantity"] = 1;
            
            // If asset, add callback params
            if (asset != null) {
                transact_params["asset-enact-hold-url"] = asset.BuildEnactURI(baseURI);
                transact_params["asset-consume-hold-url"] = asset.BuildConsumeURI(baseURI);
                transact_params["asset-cancel-hold-url"] = asset.BuildCancelURI(baseURI);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-enact-hold-url:{0}", transact_params["asset-enact-hold-url"]);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-consume-hold-url:{0}", transact_params["asset-consume-hold-url"]);
                m_log.InfoFormat("[GLOEBITMONEYMODULE] asset-cancel-hold-url:{0}", transact_params["asset-cancel-hold-url"]);
            }
            
            // U2U specific transact params
            //transact_params["seller-name-on-application"] = String.Format("{0} - {1}", recipientName, recipient.PrincipalID);
            transact_params["seller-name-on-application"] = recipientName;
            transact_params["seller-id-on-application"] = recipient.PrincipalID;
            // TODO: check for null or UUID.Zero
            if (recipient.GloebitID != null) {
                transact_params["seller-id-from-gloebit"] = recipient.GloebitID;
            }
            if (recipientEmail != String.Empty) {
                transact_params["seller-email-address"] = recipientEmail;
            }
            transact_params["buyer-id-on-application"] = sender.PrincipalID;
            if (descMap != null) {
                transact_params["platform-desc-names"] = descMap["platform-names"];
                transact_params["platform-desc-values"] = descMap["platform-values"];
                transact_params["location-desc-names"] = descMap["location-names"];
                transact_params["location-desc-values"] = descMap["location-values"];
                transact_params["transaction-desc-names"] = descMap["transaction-names"];
                transact_params["transaction-desc-values"] = descMap["transaction-values"];
            }
            
            // Subscription params
            // TODO: Stop hijacking saleType and create a new field in the asset
            if (asset != null) {
                if (asset.SaleType == 100) {
                    transact_params["automated-transaction"] = true;
                    Subscription sub = Subscription.Get(asset.PartID, m_key, m_url);
                    if (sub != null && sub.SubscriptionID != UUID.Zero) {
                        transact_params["subscription-id"] = sub.SubscriptionID;
                    } else {
                        if (sub == null) {
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U FAIL -- attempting to submit automated-transaction without a local subscription.  calling create and returning false");
                            // TODO: start storing the part description in the asset/transaction
                            // TODO: get rid of this hack
                            string objectDescription = "";
                            OSDArray nameArray = (OSDArray) descMap["transaction-names"];
                            OSDArray valueArray = (OSDArray) descMap["transaction-values"];
                            if (descMap != null) {
                                for (int i = 0; i < nameArray.Count(); i++) {
                                    if (nameArray[i] == "object-description") {
                                        objectDescription = valueArray[i];
                                        break;
                                    }
                                }
                                /*foreach (OSD name in nameArray) {
                                    if (name == "object-description") {
                                        int index = nameArray.GetIndex(name);
                                        objectDescription = descMap["transaction-values"][index];
                                    }
                                }*/
                            }
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U FAIL -- INITING SUBSCRIPTION for {0}", asset.PartName);
                            sub = Subscription.Init(asset.PartID, m_key, m_url.ToString(), asset.PartName, objectDescription);
                        }
                        CreateSubscription(sub, baseURL);
                        return false;
                    }
                }
            }
            
            HttpWebRequest request = BuildGloebitRequest("transact-u2u", "POST", sender, "application/json", transact_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U failed to create HttpWebRequest");
                return false;
                // TODO once we return, return error value
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U response: {0}", responseDataMap);

                //************ PARSE AND HANDLE TRANSACT-U2U RESPONSE *********//

                bool success = (bool)responseDataMap["success"];
                // TODO: if success=false: id, balance, product-count are invalid.  Do not set balance.
                double balance = responseDataMap["balance"].AsReal();
                string reason = responseDataMap["reason"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U success: {0} balance: {1} reason: {2}", success, balance, reason);
                if (success) {
                    asset.BuyerEndingBalance = (int)balance;
                    GloebitAssetData.Instance.Store(asset);
                } else {
                    switch(reason) {
                        case "unknown OAuth2 token":
                        //case "Missing OAuth2 token or using OAuth1.0a.  This endpoint requires OAuth2.":
                        //case "OAuth2 token has expired":
                            // The token is invalid (probably the user revoked our app through the website)
                            // so force a reauthorization next time.
                            sender.InvalidateToken();
                            break;
                        case "unknown-subscription-authorization":
                        case "subscription-authorization-pending":
                        case "subscription-authorization-declined":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U Subscription-Auth issue: '{0}'", reason);
                            break;
                        default:
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.Transact-U2U Unknown error posting transaction, reason: '{0}'", reason);
                            break;
                    }
                }

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.transactU2UCompleted(responseDataMap, sender, recipient, asset);
            }));
            
            return true;
        }
        
        /// <summary>
        /// Begins request to exchange an authorization code granted from the Authorize endpoint for an access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.
        /// This begins the second phase of the OAuth2 process.  It is activated by the redirect_uri of the Authorize function.
        /// This occurs completely behind the scenes for security purposes.
        /// </summary>
        /// <returns>The authenticated User object containing the access token necessary for enacting Gloebit functionality on behalf of this OpenSim user.</returns>
        /// <param name="user">OpenSim User for which this region/grid is asking for permission to enact Gloebit functionality.</param>
        /// <param name="auth_code">Authorization Code returned to the redirect_uri from the Gloebit Authorize endpoint.</param>
        public void CreateSubscription(Subscription subscription, Uri baseURL) {
            
            //TODO stop logging auth_code
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription Subscription:{0}", subscription);
            
            // ************ BUILD EXCHANGE ACCESS TOKEN POST REQUEST ******** //
            OSDMap sub_params = new OSDMap();

            sub_params["client_id"] = m_key;
            sub_params["client_secret"] = m_secret;
            
            sub_params["application-key"] = m_key;  // TODO: consider getting rid of this.
            if (m_key != subscription.AppKey) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription GloebitAPI.m_key:{0} differs from Subscription.AppKey:{1}", m_key, subscription.AppKey);
                return;
            }
            sub_params["local-id"] = subscription.ObjectID;
            sub_params["name"] = subscription.ObjectName;
            sub_params["description"] = subscription.Description;
            //sub_params["additional_details"] = subscription.AdditionalDetails;
            sub_params["additional-details"] = "no additional details";
            // TODO: need to test if additional-details, name or description are blank and fill them in with a default if so.  Otherwise, they
            // need to be optional on the Gloebit side.

            
            //auth_params["redirect_uri"] = BuildAuthCallbackURL(baseURL, user.AgentId).ToString();
            
            HttpWebRequest request = BuildGloebitRequest("create-subscription", "POST", null, "application/json", sub_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed to create HttpWebRequest");
                // TODO: signal error
                return;
            }
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription response: {0}", responseDataMap);

                //************ PARSE AND HANDLE CREATE SUBSCRIPTION RESPONSE *********//

                // Grab fields always included in response
                bool success = (bool)responseDataMap["success"];
                string reason = responseDataMap["reason"];
                string status = responseDataMap["status"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription success: {0} reason: {1} status: {2}", success, reason, status);
                
                if (success) {
                    string subscriptionIDStr = responseDataMap["id"];
                    bool enabled = (bool) responseDataMap["enabled"];
                    subscription.SubscriptionID = UUID.Parse(subscriptionIDStr);
                    subscription.Enabled = enabled;
                    GloebitSubscriptionData.Instance.Store(subscription);
                    if (status == "duplicate") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription duplicate request to create subscription");
                    }
                } else {
                    switch(reason) {
                        case "Unexpected DB insert integrity error.  Please try again.":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed from {0}", reason);
                            break;
                        case "different subscription exists with this app-subscription-id":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed due to different subscription with same object id -- subID:{0} name:{1} desc:{2} ad:{3} enabled:{4} ctime:{5}",
                                              responseDataMap["existing-subscription-id"], responseDataMap["existing-subscription-name"], responseDataMap["existing-subscription-description"], responseDataMap["existing-subscription-additional_details"], responseDataMap["existing-subscription-enabled"], responseDataMap["existing-subscription-ctime"]);
                            break;
                        case "Unknown DB Error":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscription failed from {0}", reason);
                            break;
                        default:
                            m_log.ErrorFormat("Unknown error posting create subscription, reason: '{0}'", reason);
                            break;
                    }
                }

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.createSubscriptionCompleted(responseDataMap, subscription);
            }));
            
            return;
        }
    
        /// <summary>
        /// Request Gloebit transaction for the gloebit amount specified from the sender to the recipient.
        /// </summary>
        /// <param name="senderID">User object for the user sending the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="senderName">OpenSim Name of the user on this grid sending the gloebits.</param>
        /// <param name="recipient">User object for the user receiving the gloebits. <see cref="GloebitAPI.User.Get(UUID)"/></param>
        /// <param name="recipientName">OpenSim Name of the user on this grid receiving the gloebits.</param>
        /// <param name="recipientEmail">Email address of the user on this grid receiving the gloebits.  Empty string if user created account without email.</param>
        /// <param name="amount">quantity of gloebits to be transacted.</param>
        /// <param name="description">Description of purpose of transaction recorded in Gloebit transaction histories.</param>
        /// <param name="asset">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <param name="transactionId">UUID provided by calling application.  This ID will be provided back to the application in any callbacks and allows for Idempotence.</param>
        /// <param name="descMap">Map of platform, location & transaction descriptors for tracking/querying and transaciton history details.  For more details, see buildTransactionDescMap helper function.</param>
        /// <param name="baseURL">Asset representing local transaction part requiring processing via callbacks.</param>
        /// <returns>true if async transactU2U web request was built and submitted successfully; false if failed to submit request;  If true, IAsyncEndpointCallback transactU2UCompleted should eventually be called with additional details on state of request.</returns>

        public bool CreateSubscriptionAuthorization(Subscription sub, User sender, string senderName, Uri baseURL, IClientAPI client) {

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization subscriptionID:{0} senderID:{1} senderName:{2} baseURL:{3}", sub.SubscriptionID, sender.PrincipalID, senderName, baseURL);
            
            
            // ************ BUILD AND SEND CREATE SUBSCRIPTION AUTHORIZATION POST REQUEST ******** //
            
            OSDMap sub_auth_params = new OSDMap();
            
            sub_auth_params["application-key"] = m_key;
            sub_auth_params["request-created"] = (int)(DateTime.UtcNow.Ticks / 10000000);  // TODO - figure out if this is in the right units
            //sub_auth_params["username-on-application"] = String.Format("{0} - {1}", senderName, sender.PrincipalID);
            sub_auth_params["username-on-application"] = senderName;
            sub_auth_params["user-id-on-application"] = sender.PrincipalID;
            sub_auth_params["additional-details"] = "no additional details";
            sub_auth_params["subscription-id"] = sub.SubscriptionID;
            // TODO: need to test if additional-details is blank and fill it in with a default if so.  Otherwise, it
            // needs to be optional on the Gloebit side.
            
            HttpWebRequest request = BuildGloebitRequest("create-subscription-authorization", "POST", sender, "application/json", sub_auth_params);
            if (request == null) {
                // ERROR
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization failed to create HttpWebRequest");
                return false;
                // TODO once we return, return error value
            }

            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization about to BeginGetResponse");
            // **** Asynchronously make web request **** //
            IAsyncResult r = request.BeginGetResponse(GloebitWebResponseCallback,
			                                          new GloebitRequestState(request, 
			                        delegate(OSDMap responseDataMap) {
                                        
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization response: {0}", responseDataMap);

                //************ PARSE AND HANDLE CREATE SUBSCRIPTION AUTHORIZATION RESPONSE *********//

                // Grab fields always included in response
                bool success = (bool)responseDataMap["success"];
                string reason = responseDataMap["reason"];
                string status = responseDataMap["status"];
                m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization success: {0} reason: {1} status: {2}", success, reason, status);
                
                if (success) {
                    string subscriptionAuthIDStr = responseDataMap["id"];
                    // TODO: if we decide to store auths, this would be a place to do so.
                    // sub.SubscriptionID = UUID.Parse(subscriptionIDStr);
                    // GloebitSubscriptionData.Instance.Store(sub);
                    if (status == "duplicate") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization duplicate request to create subscription");
                    } else if (status == "duplicate-and-already-approved-by-user") {
                        m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization duplicate request to create subscription - subscription has already been approved by user.");
                    } else if (status == "duplicate-and-previously-declined-by-user") {
                        m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization SUCCESS & FAILURE - user previously declined authorization -- consider if app should re-request or if that is harrassing user or has Gloebit API reset this automatcially?. status:{0} reason:{1}", status, reason);
                    }
                    
                    string sPending = responseDataMap["pending"];
                    string sEnabled = responseDataMap["enabled"];
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization SUCCESS pending:{0}, enabled:{1}.", sPending, sEnabled);
                } else {
                    switch(status) {
                        case "cannot-transact":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - no transact permissions on this user. status:{0} reason:{1}", status, reason);
                            break;
                        case "subscription-not-found":
                        case "mismatched-application-key":
                        case "mis-matched-subscription-ids":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - could not properly identify subscription - status:{0} reason:{1}", status, reason);
                            break;
                        case "subscription-disabled":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - app has disabled this subscription. status:{0} reason:{1}", status, reason);
                            break;
                        case "duplicate-and-previously-declined-by-user":
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED - user previously declined authorization -- consider if app should re-request or if that is harrassing user. status:{0} reason:{1}", status, reason);
                            break;
                        default:
                            switch(reason) {
                                case "Unexpected DB insert integrity error.  Please try again.":
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization FAILED from {0}", reason);
                                    break;
                                case "Unknown DB Error":
                                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.CreateSubscriptionAuthorization failed from {0}", reason);
                                    break;
                                default:
                                    m_log.ErrorFormat("Unknown error posting create subscription authorization, reason: '{0}'", reason);
                                    break;
                            }
                            break;
                    }
                }

                // TODO - decide if we really want to issue this callback even if the token was invalid
                m_asyncEndpointCallbacks.createSubscriptionAuthorizationCompleted(responseDataMap, sub, sender, client);
            }));
            
            return true;
        }
        

        /// <summary>
        /// Builds a URI for a user to purchase gloebits
        /// </summary>
        /// <returns>The fully constructed url with arguments for receiving a callback when the purchase is complete.</returns>
        public Uri BuildPurchaseURI(Uri callbackBaseURL, User u) {
            UriBuilder purchaseUri = new UriBuilder(m_url);
            purchaseUri.Path = "/purchase";
            UriBuilder callbackUrl = new UriBuilder(callbackBaseURL);
            callbackUrl.Path = "/gloebit/buy_complete";
            callbackUrl.Query = String.Format("agentId={0}", u.PrincipalID);
            purchaseUri.Query = String.Format("reset&r={0}&return-to={1}", m_keyAlias, callbackUrl.Uri);
            return purchaseUri.Uri;
        }
 
        /***********************************************/
        /********* GLOEBIT API HELPER FUNCTIONS ********/
        /***********************************************/
    
        // TODO: OSDMap or Dictionary for params
    
        /// <summary>
        /// Build an HTTPWebRequest for a Gloebit endpoint.
        /// </summary>
        /// <param name="relative_url">endpoint & query args.</param>
        /// <param name="method">HTTP method for request -- eg: "GET", "POST".</param>
        /// <param name="user">User object for this authenticated user if one exists.</param>
        /// <param name="content_type">content type of post/put request  -- eg: "application/json", "application/x-www-form-urlencoded".</param>
        /// <param name="paramMap">parameter map for body of request.</param>
        private HttpWebRequest BuildGloebitRequest(string relativeURL, string method, User user, string contentType = "", OSDMap paramMap = null) {
            
            // combine Gloebit base url with endpoint and query args in relative url.
            Uri requestURI = new Uri(m_url, relativeURL);
        
            // Create http web request from URL
            HttpWebRequest request = (HttpWebRequest) WebRequest.Create(requestURI);
        
            // Add authorization header
            if (user != null && user.GloebitToken != "") {
                request.Headers.Add("Authorization", String.Format("Bearer {0}", user.GloebitToken));
            }
        
            // Set request method and body
            request.Method = method;
            switch (method) {
                case "GET":
                    m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest GET relativeURL:{0}", relativeURL);
                    break;
                case "POST":
                case "PUT":
                    string paramString = "";
                    byte[] postData = null;
                    request.ContentType = contentType;
                
                    // Build paramString in proper format
                    if (paramMap != null) {
                        if (contentType == "application/x-www-form-urlencoded") {
                            paramString = BuildURLEncodedParamString(paramMap);
                        } else if (contentType == "application/json") {
                            paramString = OSDParser.SerializeJsonString(paramMap);
                        } else {
                            // ERROR - we are not handling this content type properly
                            m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, unrecognized content type:{1}", relativeURL, contentType);
                            return null;
                        }
                
                        // Byte encode paramString and write to requestStream
                        postData = System.Text.Encoding.UTF8.GetBytes(paramString);
                        request.ContentLength = postData.Length;
                        // TODO: look into BeginGetRequestStream()
                        using (Stream s = request.GetRequestStream()) {
                            s.Write(postData, 0, postData.Length);
                        }
                    } else {
                        // Probably should be a GET request if it has no paramMap
                        m_log.WarnFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, Empty paramMap on {1} request", relativeURL, method);
                    }
                    break;
                default:
                    // ERROR - we are not handling this request type properly
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildGloebitRequest relativeURL:{0}, unrecognized web request method:{1}", relativeURL, method);
                    return null;
            }
            return request;
        }
        
        /// <summary>
        /// Build an application/x-www-form-urlencoded string from the paramMap.
        /// </summary>
        /// <param name="ParamMap">Parameters to be encoded.</param>
        private string BuildURLEncodedParamString(OSDMap paramMap) {
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.BuildURLEncodedParamString building from paramMap:{0}:", paramMap);
            StringBuilder paramBuilder = new StringBuilder();
            foreach (KeyValuePair<string, OSD> p in (OSDMap)paramMap) {
                if(paramBuilder.Length != 0) {
                    paramBuilder.Append('&');
                }
                paramBuilder.AppendFormat("{0}={1}", HttpUtility.UrlEncode(p.Key), HttpUtility.UrlEncode(p.Value.ToString()));
            }
            return( paramBuilder.ToString() );
        }
        
        /***********************************************/
        /** GLOEBIT ASYNCHRONOUS API HELPER FUNCTIONS **/
        /***********************************************/
        
        /// <summary>
        /// Handles asynchronous return from web request BeginGetResponse.
        /// Retrieves response stream and asynchronously begins reading response stream.
        /// </summary>
        /// <param name="ar">State details compiled as this web request is processed.</param>
        public void GloebitWebResponseCallback(IAsyncResult ar) {
            
            m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback");
            
            // Get the RequestState object from the async result.
            GloebitRequestState myRequestState = (GloebitRequestState) ar.AsyncState;
            HttpWebRequest req = myRequestState.request;
            
            // Call EndGetResponse, which produces the WebResponse object
            //  that came from the request issued above.
            try
            {
                HttpWebResponse resp = (HttpWebResponse)req.EndGetResponse(ar);

                //  Start reading data from the response stream.
                // TODO: look into BeginGetResponseStream();
                Stream responseStream = resp.GetResponseStream();
                myRequestState.responseStream = responseStream;

                // TODO: Do I need to check the CanRead property before reading?

                //  Begin reading response into myRequestState.BufferRead
                // TODO: May want to make use of iarRead for calls by syncronous functions
                IAsyncResult iarRead = responseStream.BeginRead(myRequestState.bufferRead, 0, GloebitRequestState.BUFFER_SIZE, GloebitReadCallBack, myRequestState);

                // TODO: on any failure/exception, propagate error up and provide to user in friendly error message.
            }
            catch (ArgumentNullException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback ArgumentNullException e:{0}", e.Message);
            }
            catch (WebException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback WebException e:{0} URI:{1}", e.Message, req.RequestUri);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] response:{0}", e.Response);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] e:{0}", e.ToString ());
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] source:{0}", e.Source);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] stack_trace:{0}", e.StackTrace);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] status:{0}", e.Status);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] target_site:{0}", e.TargetSite);
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] data_count:{0}", e.Data.Count);
            }
            catch (InvalidOperationException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback InvalidOperationException e:{0}", e.Message);
            }
            catch (ArgumentException e) {
                m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitWebResponseCallback ArgumentException e:{0}", e.Message);
            }

        }
        
        /// <summary>
        /// Handles asynchronous return from web request response stream BeginRead().
        /// Retrieves and stores buffered read, or closes stream and passes requestState to requestState.continuation().
        /// </summary>
        /// <param name="ar">State details compiled as this web request is processed.</param>
        private void GloebitReadCallBack(IAsyncResult ar)
        {
            // m_log.InfoFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitReadCallback");
            
            // Get the RequestState object from AsyncResult.
            GloebitRequestState myRequestState = (GloebitRequestState)ar.AsyncState;
            Stream responseStream = myRequestState.responseStream;
            
            // Handle data read.
            int bytesRead = responseStream.EndRead( ar );
            if (bytesRead > 0)
            {
                // Decode and store the bytesRead in responseData
                Char[] charBuffer = new Char[GloebitRequestState.BUFFER_SIZE];
                int len = myRequestState.streamDecoder.GetChars(myRequestState.bufferRead, 0, bytesRead, charBuffer, 0);
                String str = new String(charBuffer, 0, len);
                myRequestState.responseData.Append(str);
                
                // Continue reading data until
                // responseStream.EndRead returns 0 for end of stream.
                // TODO: should we be doing anything with result???
                IAsyncResult result = responseStream.BeginRead(myRequestState.bufferRead, 0, GloebitRequestState.BUFFER_SIZE, GloebitReadCallBack, myRequestState);
            }
            else
            {
                // Done Reading
                
                // Close down the response stream.
                responseStream.Close();
                
                if (myRequestState.responseData.Length <= 0) {
                    // TODO: Is this necessarily an error if we don't have data???
                    m_log.ErrorFormat("[GLOEBITMONEYMODULE] GloebitAPI.GloebitReadCallback error: No Data");
                    // TODO: signal error
                }
                
                if (myRequestState.continuation != null) {
                    OSDMap responseDataMap = (OSDMap)OSDParser.DeserializeJson(myRequestState.responseData.ToString());
                    myRequestState.continuation(responseDataMap);
                }
            }
        }
        
        // TODO: These functions should probably be moved to the money module.
        
        /// <summary>
        /// Sends a message with url to user.
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="title">string title of message we are sending with the url</param>
        /// <param name="body">string body of message we are sending with the url</param>
        /// <param name="uri">full url we are sending to the client</param>
        public static void SendUrlToClient(IClientAPI client, string title, string body, Uri uri)
        {
            string imMessage = String.Format("{0}\n\n{1}", title, body);
            UUID fromID = UUID.Zero;
            string fromName = String.Empty;
            UUID toID = client.AgentId;
            bool isFromGroup = false;
            UUID imSessionID = toID;     // Don't know what this is used for.  Saw it hacked to agent id in friendship module
            bool isOffline = true;          // Don't know what this is for.  Should probably try both.
            bool addTimestamp = false;
            GridInstantMessage im = new GridInstantMessage(client.Scene, fromID, fromName, toID, (byte)InstantMessageDialog.GotoUrl, isFromGroup, imMessage, imSessionID, isOffline, Vector3.Zero, Encoding.UTF8.GetBytes(uri.ToString() + "\0"), addTimestamp);
            client.SendInstantMessage(im);
        }
        
        // TODO: This is a bit of a hack due to lack of access to non-static private variables m_key and m_url
        /// <summary>
        /// This should probably be moved to the money module.
        /// Sends a message with url to user.
        /// builds url from m_url, m_key and resourceID
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="title">string title of message we are sending with the url</param>
        /// <param name="body">string body of message we are sending with the url</param>
        /// <param name="resourceID">url resourceID (everything after the domain) we are sending to the client.  No leading '/'</param>
/*        public void SendUrlToClient(IClientAPI client, string title, string body, string resourceID)
        {
            Uri request_uri = new Uri(m_url, String.Format("{0}/{1}", m_key, resourceID));
            SendUrlToClient(client, title, body, request_uri);
        }
*/
        // TODO: This should become an interface function and moved to the Money Module
        /// <summary>
        /// Request a subscriptin authorization from a user.
        /// This specifically sends a message with a clickable URL to the client.
        /// </summary>
        /// <param name="client">IClientAPI of client we are sending the URL to</param>
        /// <param name="title">string title of message we are sending with the url</param>
        /// <param name="body">string body of message we are sending with the url</param>
        /// <param name="resourceID">url resourceID (everything after the domain) we are sending to the client.  No leading '/'</param>
        public void SendSubscriptionAuthorizationToClient(IClientAPI client, string subAuthID, Subscription sub)
        {
            // Build the URL -- consider making a helper to be done in the API once we move this to the GMM
            Uri request_uri = new Uri(m_url, String.Format("authorize-subscription/{0}/", subAuthID));
            
            if (client != null) {
                // TODO: adjust our wording
                string title = "GLOEBIT Authorize Debit Script:";
                string body = String.Format("To authorize this object:\n   {0}\n   {1}\n\nPlease visit this web page:", sub.ObjectName, sub.ObjectID);
                
                SendUrlToClient(client, title, body, request_uri);
            } else {
                // TODO: what should we do in this case?  Ideally, Gloebit has also emailed the user when this request was created.
                // Perhaps, when user is not logged in, add to queue and send when user next logs in.
            }
        }
    }
}
