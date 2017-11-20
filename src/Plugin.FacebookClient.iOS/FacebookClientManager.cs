using Plugin.FacebookClient.Abstractions;
using System;
using Facebook.LoginKit;
using Facebook;
using UIKit;
using System.Threading.Tasks;
using System.Linq;
using Facebook.ShareKit;
using Foundation;
using Facebook.CoreKit;
using System.Collections.Generic;
using ExternalAccessory;
using System.IO;
using AVFoundation;


namespace Plugin.FacebookClient
{
    /// <summary>
    /// Implementation for FacebookClient
    /// </summary>
    public class FacebookClientManager : NSObject, IFacebookClient, ISharingDelegate
    {
        TaskCompletionSource<FacebookResponse<Dictionary<string, object>>> _userDataTcs;
        TaskCompletionSource<FacebookResponse<Dictionary<string, object>>> _shareTcs;
        TaskCompletionSource<FacebookResponse<string>> _requestTcs;
        TaskCompletionSource<FacebookResponse<string>> _postTcs;
        TaskCompletionSource<FacebookResponse<string>> _deleteTcs;


        public event EventHandler<FBEventArgs<Dictionary<string, object>>> OnUserData = delegate { };

        public event EventHandler<FBEventArgs<bool>> OnLogin = delegate { };

        public event EventHandler<FBEventArgs<bool>> OnLogout = delegate { };

        public event EventHandler<FBEventArgs<Dictionary<string, object>>> OnSharing = delegate { };

        public event EventHandler<FBEventArgs<string>> OnRequestData = delegate { };
        public event EventHandler<FBEventArgs<string>> OnPostData = delegate { };
        public event EventHandler<FBEventArgs<string>> OnDeleteData = delegate { };

        LoginManager loginManager = new LoginManager();
        FacebookPendingAction<Dictionary<string, object>> pendingAction;


        public string[] ActivePermissions
        {
            get
            {
                return AccessToken.CurrentAccessToken == null ? new string[] { } : AccessToken.CurrentAccessToken.Permissions.Select(p => $"{p}").ToArray();
            }
        }

        public string[] DeclinedPermissions
        {
            get
            {
                return AccessToken.CurrentAccessToken == null ? new string[] { } : AccessToken.CurrentAccessToken.DeclinedPermissions.Select(p => $"{p}").ToArray();
            }
        }

        public bool IsLoggedIn
        {
            get
            {
                return AccessToken.CurrentAccessToken != null;
            }
        }

        public string ActiveToken
        {
            get
            {
                return AccessToken.CurrentAccessToken?.TokenString ?? string.Empty;
            }
        }

        public string ActiveUserId
        {
            get
            {
                return AccessToken.CurrentAccessToken?.UserID ?? string.Empty;
            }
        }

        public bool HasPermissions(string[] permissions)
        {
            if (!IsLoggedIn)
                return false;

            var currentPermissions = AccessToken.CurrentAccessToken.Permissions;

            return permissions.All(p => AccessToken.CurrentAccessToken.HasGranted(p));
        }

        public bool VerifyPermission(string permission)
        {

            return IsLoggedIn && (AccessToken.CurrentAccessToken.HasGranted(permission));

        }
        public static void Initialize(UIApplication app,NSDictionary options)
        {
            ApplicationDelegate.SharedInstance.FinishedLaunching(app, options);
        }
        public static void OnActivated()
        {
            AppEvents.ActivateApp();
        }
        public static void OpenUrl(UIApplication app, NSUrl url, NSDictionary options)
        {
            ApplicationDelegate.SharedInstance.OpenUrl(app, url, $"{options["UIApplicationOpenURLOptionsSourceApplicationKey"]}", null);
        }

        public static void OpenUrl(UIApplication application, NSUrl url, string sourceApplication, NSObject annotation)
        {
            ApplicationDelegate.SharedInstance.OpenUrl(application, url, sourceApplication, annotation);
        }
        
        public void DidComplete(ISharing sharer, NSDictionary results)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>();
            foreach (var r in results)
            {
                parameters.Add($"{r.Key}", $"{r.Value}");
            }
            var fbArgs = new FBEventArgs<Dictionary<string, object>>(parameters, FacebookActionStatus.Completed);
            OnSharing(this, fbArgs);
            _shareTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
        }

        public void DidFail(ISharing sharer, NSError error)
        {
            var fbArgs = new FBEventArgs<Dictionary<string, object>>(null, FacebookActionStatus.Error, error.Description);
            OnSharing(this, fbArgs);
            _shareTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
        }

        public void DidCancel(ISharing sharer)
        {
            var fbArgs = new FBEventArgs<Dictionary<string, object>>(null, FacebookActionStatus.Canceled);

            OnSharing(this, fbArgs);
            _shareTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
        }

        public async Task<FacebookResponse<bool>> LoginAsync(string[] permissions, FacebookPermissionType permissionType = FacebookPermissionType.Read)
        {
            var retVal = IsLoggedIn;
            FacebookActionStatus status = FacebookActionStatus.Error;
            if (!retVal || !HasPermissions(permissions))
            {
                var window = UIApplication.SharedApplication.KeyWindow;
                var vc = window.RootViewController;
                while (vc.PresentedViewController != null)
                {
                    vc = vc.PresentedViewController;
                }

                LoginManagerLoginResult result = await (permissionType == FacebookPermissionType.Read ? loginManager.LogInWithReadPermissionsAsync(permissions, vc) : loginManager.LogInWithPublishPermissionsAsync(permissions, vc));

                if (result.IsCancelled)
                {
                    retVal = false;
                    status = FacebookActionStatus.Canceled;
                }
                else
                {
                    //result.GrantedPermissions.h
                    retVal = HasPermissions(result.GrantedPermissions.Select(p => $"{p}").ToArray());

                    status = retVal ? FacebookActionStatus.Completed : FacebookActionStatus.Unauthorized;
                }

            }
            else
            {

                status = FacebookActionStatus.Completed;

            }
            var fbArgs = new FBEventArgs<bool>(retVal, status);
            OnLogin(this, fbArgs);

            pendingAction?.Execute();

            pendingAction = null;


            return new FacebookResponse<bool>(fbArgs);
        }



        public async Task<FacebookResponse<Dictionary<string, object>>> SharePhotoAsync(byte[] photoBytes, string caption = "")
        {
            _shareTcs = new TaskCompletionSource<FacebookResponse<Dictionary<string, object>>>();
            Dictionary<string, object> parameters = new Dictionary<string, object>()
            {
                {"photo",photoBytes},

            };

            if (!string.IsNullOrEmpty(caption))
            {
                parameters.Add("caption", caption);
            }

          return await PerformAction(RequestSharePhoto, parameters, _shareTcs.Task, FacebookPermissionType.Publish, new string[] { "publish_actions" });
        }

        public async Task<FacebookResponse<Dictionary<string, object>>> ShareAsync(FacebookShareContent shareContent)
        {
            _shareTcs = new TaskCompletionSource<FacebookResponse<Dictionary<string, object>>>();
            Dictionary<string, object> parameters = new Dictionary<string, object>()
            {
                {"content",shareContent}

            };

            return await PerformAction(RequestShare, parameters, _shareTcs.Task, FacebookPermissionType.Publish, new string[] { "publish_actions" });
        }
        void RequestShare(Dictionary<string, object> paramsDictionary)
        {
            if (paramsDictionary.TryGetValue("content", out object shareContent) && shareContent is FacebookShareContent)
            {
                ISharingContent content = null;
                if (shareContent is FacebookShareLinkContent)
                {
                    FacebookShareLinkContent linkContent = shareContent as FacebookShareLinkContent;
                    ShareLinkContent sLinkContent = new ShareLinkContent();


                    if (linkContent.Quote != null)
                    {
                        sLinkContent.Quote=linkContent.Quote;
                    }

                    if (linkContent.ContentLink != null)
                    {
                        sLinkContent.SetContentUrl(linkContent.ContentLink);
                    }

                    if (!string.IsNullOrEmpty(linkContent.Hashtag))
                    {
                        var shareHashTag = new Hashtag();
                        shareHashTag.StringRepresentation = linkContent.Hashtag;
                        sLinkContent.Hashtag = shareHashTag;
                    }

                    if (linkContent.PeopleIds != null && linkContent.PeopleIds.Length > 0)
                    {
                        sLinkContent.SetPeopleIds(linkContent.PeopleIds);
                    }

                    if (!string.IsNullOrEmpty(linkContent.PlaceId))
                    {
                        sLinkContent.SetPlaceId(linkContent.PlaceId);
                    }

                    if (!string.IsNullOrEmpty(linkContent.Ref))
                    {
                        sLinkContent.SetRef(linkContent.Ref);
                    }

                    content = sLinkContent;
                }
                else if (shareContent is FacebookSharePhotoContent)
                {

                    FacebookSharePhotoContent photoContent = shareContent as FacebookSharePhotoContent;

                    SharePhotoContent sharePhotoContent = new SharePhotoContent();

                    if (photoContent.Photos != null && photoContent.Photos.Length > 0)
                    {
                        List<SharePhoto> photos = new List<SharePhoto>();
                        foreach (var p in photoContent.Photos)
                        {

                            if (p.ImageUrl != null && !string.IsNullOrEmpty(p.ImageUrl.AbsoluteUri))
                            {
                                SharePhoto photoFromUrl = SharePhoto.From(p.ImageUrl, true);

                                if (!string.IsNullOrEmpty(p.Caption))
                                {
                                    photoFromUrl.Caption = p.Caption;
                                }

                                photos.Add(photoFromUrl);
                            }

                            if (p.Image != null)
                            {
                                UIImage image = null;

                                var imageBytes = p.Image as byte[];

                                if (imageBytes != null)
                                {
                                    using (var data = NSData.FromArray(imageBytes))
                                        image = UIImage.LoadFromData(data);

                                    SharePhoto sPhoto = Facebook.ShareKit.SharePhoto.From(image, true);

                                    if (!string.IsNullOrEmpty(p.Caption))
                                    {
                                        sPhoto.Caption = p.Caption;
                                    }


                                    photos.Add(sPhoto);
                                }

                            }

                        }

                        if(photos.Count >0)
                        {
                            sharePhotoContent.Photos = photos.ToArray();
                        }

                    }

                    if (photoContent.ContentLink != null)
                    {
                        sharePhotoContent.SetContentUrl(photoContent.ContentLink);
                    }

                    if (!string.IsNullOrEmpty(photoContent.Hashtag))
                    {
                        var shareHashTag = new Hashtag();
                        shareHashTag.StringRepresentation = photoContent.Hashtag;
                        sharePhotoContent.SetHashtag(shareHashTag);
                    }

                    if (photoContent.PeopleIds != null && photoContent.PeopleIds.Length > 0)
                    {
                        sharePhotoContent.SetPeopleIds(photoContent.PeopleIds);
                    }

                    if (!string.IsNullOrEmpty(photoContent.PlaceId))
                    {
                        sharePhotoContent.SetPlaceId(photoContent.PlaceId);
                    }

                    if (!string.IsNullOrEmpty(photoContent.Ref))
                    {
                        sharePhotoContent.SetRef(photoContent.Ref);
                    }

                    content = sharePhotoContent;
                }
                else if (shareContent is FacebookShareVideoContent)
                {
                    FacebookShareVideoContent videoContent = shareContent as FacebookShareVideoContent;
                    ShareVideoContent shareVideoContent = new ShareVideoContent();


                    if (videoContent.PreviewPhoto != null)
                    {
                        if (videoContent.PreviewPhoto.ImageUrl != null && !string.IsNullOrEmpty(videoContent.PreviewPhoto.ImageUrl.AbsoluteUri))
                        {
                            SharePhoto photoFromUrl = Facebook.ShareKit.SharePhoto.From(videoContent.PreviewPhoto.ImageUrl, true); 

                            if (!string.IsNullOrEmpty(videoContent.PreviewPhoto.Caption))
                            {
                                photoFromUrl.Caption =videoContent.PreviewPhoto.Caption;
                            }

                            shareVideoContent.PreviewPhoto = photoFromUrl;
                        }

                        if (videoContent.PreviewPhoto.Image != null)
                        {
                            UIImage image = null;

                            var imageBytes = videoContent.PreviewPhoto.Image as byte[];

                            if (imageBytes != null)
                            {
                                using (var data = NSData.FromArray(imageBytes))
                                    image = UIImage.LoadFromData(data);

                                SharePhoto photo = Facebook.ShareKit.SharePhoto.From(image, true);

                                if (!string.IsNullOrEmpty(videoContent.PreviewPhoto.Caption))
                                {
                                    photo.Caption = videoContent.PreviewPhoto.Caption;
                                }


                                shareVideoContent.PreviewPhoto = photo;
                            }
                          
                        }
                        
                    }

                    if (videoContent.Video != null)
                    {
                        
                        if (videoContent.Video.LocalUrl != null)
                        {
                            shareVideoContent.Video = ShareVideo.From(videoContent.Video.LocalUrl);
                        }

                        
                    }

                    if (videoContent.ContentLink != null)
                    {
                        shareVideoContent.SetContentUrl(videoContent.ContentLink);
                    }

                    if (!string.IsNullOrEmpty(videoContent.Hashtag))
                    {
                        var shareHashTag = new Hashtag();
                        shareHashTag.StringRepresentation = videoContent.Hashtag;
                        shareVideoContent.SetHashtag(shareHashTag);
                    }

                    if (videoContent.PeopleIds != null && videoContent.PeopleIds.Length > 0)
                    {
                        shareVideoContent.SetPeopleIds(videoContent.PeopleIds);
                    }

                    if (!string.IsNullOrEmpty(videoContent.PlaceId))
                    {
                        shareVideoContent.SetPlaceId(videoContent.PlaceId);
                    }

                    if (!string.IsNullOrEmpty(videoContent.Ref))
                    {
                        shareVideoContent.SetRef(videoContent.Ref);
                    }

                    content = shareVideoContent;
                }

                if (content != null)
                {
                    
                    ShareAPI.Share(content, this);
                }
            }
        }

        void RequestSharePhoto(Dictionary<string, object> paramsDictionary)
        {
            if (paramsDictionary != null && paramsDictionary.ContainsKey("photo"))
            {
                UIImage image = null;

                var imageBytes = paramsDictionary["photo"] as byte[];

                if (imageBytes != null)
                {
                    using (var data = NSData.FromArray(imageBytes))
                      image = UIImage.LoadFromData(data);
                }
                

                SharePhoto photo = Facebook.ShareKit.SharePhoto.From(image, true);
           
                if (paramsDictionary.ContainsKey("caption"))
                {
                    photo.Caption = $"{paramsDictionary["caption"]}";
                }

         
                var photoContent = new SharePhotoContent()
                {
                    Photos = new SharePhoto[] { photo }

                };
               
                ShareAPI.Share(photoContent, this);
            }

        }

        public async Task<FacebookResponse<string>> QueryDataAsync(string path, string[] permissions, IDictionary<string, string> parameters = null, string version = null)
        {
            _requestTcs = new TaskCompletionSource<FacebookResponse<string>>();
            Dictionary<string, object> paramDict = new Dictionary<string, object>()
            {
                {"path", path},
                {"parameters",parameters},
                {"method", FacebookHttpMethod.Get},
                {"version", version}
            };
            return await PerformAction<FacebookResponse<string>>(RequestData, paramDict, _requestTcs.Task, FacebookPermissionType.Read, permissions);
        }

        public async Task<FacebookResponse<string>> PostDataAsync(string path, string[] permissions, IDictionary<string, string> parameters = null, string version = null)
        {
            _postTcs = new TaskCompletionSource<FacebookResponse<string>>();
            Dictionary<string, object> paramDict = new Dictionary<string, object>()
            {
                {"path", path},
                {"parameters",parameters},
                {"method", FacebookHttpMethod.Post},
                {"version", version}
            };
            return await PerformAction<FacebookResponse<string>>(RequestData, paramDict, _postTcs.Task, FacebookPermissionType.Publish, permissions);
        }

        public async Task<FacebookResponse<string>> DeleteDataAsync(string path, string[] permissions, IDictionary<string, string> parameters = null, string version = null)
        {
            _deleteTcs = new TaskCompletionSource<FacebookResponse<string>>();
            Dictionary<string, object> paramDict = new Dictionary<string, object>()
            {
                {"path", path},
                {"parameters",parameters},
                {"method", FacebookHttpMethod.Delete },
                {"version", version}
            };
            return await PerformAction<FacebookResponse<string>>(RequestData, paramDict, _deleteTcs.Task, FacebookPermissionType.Publish, permissions);
        }



        void RequestData(Dictionary<string, object> pDictionary)
        {
            string path = $"{pDictionary["method"]}";
            string version = $"{pDictionary["version"]}";
            Dictionary<string, string> paramDict = pDictionary["parameters"] as Dictionary<string, string>;
            FacebookHttpMethod? method = pDictionary["method"] as FacebookHttpMethod?;
            var currentTcs = _requestTcs;
            var onEvent = OnRequestData;
            var httpMethod = "GET";
            if (method != null)
            {
                switch (method)
                {
                    case FacebookHttpMethod.Get:
                        httpMethod = "GET";
                        break;
                    case FacebookHttpMethod.Post:
                        httpMethod = "POST";
                        onEvent = OnPostData;
                        currentTcs = _postTcs;
                        break;
                    case FacebookHttpMethod.Delete:
                        httpMethod = "DELETE";
                        onEvent = OnDeleteData;
                        currentTcs = _deleteTcs;
                        break;
                }

            }

            if (string.IsNullOrEmpty(path))
            {
                var fbResponse = new FBEventArgs<string>(null, FacebookActionStatus.Error, "Graph query path not specified");
                onEvent?.Invoke(CrossFacebookClient.Current, fbResponse);
                currentTcs?.TrySetResult(new FacebookResponse<string>(fbResponse));
                return;
            }

         

            

            NSMutableDictionary parameters=null;

            if (paramDict != null)
            {
                parameters = new NSMutableDictionary();
                foreach (var p in paramDict)
                {
                    var key = $"{p}";
                    parameters.Add(new NSString(key), new NSString($"{paramDict[key]}"));
                }
            }

            var graphRequest = string.IsNullOrEmpty(version)?new GraphRequest(path, parameters, httpMethod): new GraphRequest(path, parameters, AccessToken.CurrentAccessToken.TokenString,version, httpMethod);
            var requestConnection = new GraphRequestConnection();
            requestConnection.AddRequest(graphRequest, (connection, result, error) =>
            {
                
                if (error == null)
                {
                    var fbResponse = new FBEventArgs<string>(result.ToString(), FacebookActionStatus.Completed);
                    onEvent?.Invoke(CrossFacebookClient.Current, fbResponse);
                    currentTcs?.TrySetResult(new FacebookResponse<string>(fbResponse));
                }
                else
                {
                    var fbResponse = new FBEventArgs<string>(null, FacebookActionStatus.Error, error.ToString());
                    onEvent?.Invoke(CrossFacebookClient.Current, fbResponse);
                    currentTcs?.TrySetResult(new FacebookResponse<string>(fbResponse));
                }
            });
            requestConnection.Start();
        }
        void RequestUserData(Dictionary<string, object> fieldsDictionary)
        {
            string[] fields = new string[] { };
            var extraParams = string.Empty;
            if (fieldsDictionary != null && fieldsDictionary.ContainsKey("fields"))
            {
                fields = fieldsDictionary["fields"] as string[];
                if (fields.Length > 0)
                    extraParams = $"?fields={string.Join(",", fields)}";
            }

            //email,first_name,gender,last_name,birthday

            if (AccessToken.CurrentAccessToken != null)
            {
                Dictionary<string, object> userData = new Dictionary<string, object>();
                var graphRequest = new GraphRequest($"/me{extraParams}", null, AccessToken.CurrentAccessToken.TokenString, null, "GET");
                var requestConnection = new GraphRequestConnection();
                requestConnection.AddRequest(graphRequest, (connection, result, error) =>
                {
                    if (error == null)
                    {
                        for (int i = 0; i < fields.Length; i++)
                        {
                            userData.Add(fields[i], $"{result.ValueForKey(new NSString(fields[i]))}");
                        }
                        userData.Add("user_id", AccessToken.CurrentAccessToken.UserID);
                        userData.Add("token", AccessToken.CurrentAccessToken.TokenString);
                        var fbArgs = new FBEventArgs<Dictionary<string, object>>(userData, FacebookActionStatus.Completed);
                        OnUserData(this,fbArgs);
                        _userDataTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
                        
                    }
                    else
                    {
                        var fbArgs = new FBEventArgs<Dictionary<string, object>>(null, FacebookActionStatus.Error, $"Facebook User Data Request Failed - {error.Code} - {error.Description}");
                        OnUserData(this,fbArgs );
                        _userDataTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
                       
                    }


                });
                requestConnection.Start();
            }
            else
            {
                var fbArgs = new FBEventArgs<Dictionary<string, object>>(null, FacebookActionStatus.Canceled);
                OnUserData(this, null);
                _userDataTcs?.TrySetResult(new FacebookResponse<Dictionary<string, object>>(fbArgs));
            }


        }

        async Task<T> PerformAction<T>(Action<Dictionary<string, object>> action, Dictionary<string, object> parameters, Task<T> task, FacebookPermissionType permissionType, string[] permissions) 
        {
            pendingAction = null;
            if (permissions == null)
            {
                action(parameters);
            }
            else
            {
                bool authorized = HasPermissions(permissions);

                if (!authorized)
                {
                    pendingAction = new FacebookPendingAction<Dictionary<string, object>>(action, parameters);
                    await LoginAsync(permissions, permissionType);
                }
                else
                {
                    action(parameters);
                }
            }

            return await task;

        }


        async Task<FBEventArgs<Dictionary<string, object>>> PerformAction(Action<Dictionary<string, object>> action, Dictionary<string, object> parameters,Task<FBEventArgs<Dictionary<string, object>>> task, FacebookPermissionType permissionType, string[] permissions)
        {
            pendingAction = null;
            if (permissions == null)
            {
                action(parameters);
            }
            else
            {
                bool authorized = HasPermissions(permissions);

                if (!authorized)
                {
                    pendingAction = new FacebookPendingAction<Dictionary<string, object>>(action, parameters);
                    await LoginAsync(permissions, permissionType);

                }
                else
                {
                    action(parameters);
                }
            }
           

            return await task;
        }

        public void Logout()
        {
            if (IsLoggedIn)
                loginManager.LogOut();

            OnLogout(this, new FBEventArgs<bool>(true, FacebookActionStatus.Completed));
        }

        public async Task<FacebookResponse<Dictionary<string, object>>> RequestUserDataAsync(string[] fields, string[] permissions, FacebookPermissionType permissionType = FacebookPermissionType.Read)
        {
            _userDataTcs = new TaskCompletionSource<FacebookResponse<Dictionary<string, object>>>();
            Dictionary<string, object> parameters = new Dictionary<string, object>()
            {
                {"fields",fields}
            };

           return await PerformAction(RequestUserData, parameters,_userDataTcs.Task, FacebookPermissionType.Read, permissions);
        }

        public void LogEvent(string name)
        {
            AppEvents.LogEvent(name);
        }

      
    }
}