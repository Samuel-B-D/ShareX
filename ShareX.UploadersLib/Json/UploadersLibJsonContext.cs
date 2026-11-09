#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib.Json;
using ShareX.UploadersLib.FileUploaders;
using ShareX.UploadersLib.ImageUploaders;
using ShareX.UploadersLib.TextUploaders;
using ShareX.UploadersLib.URLShorteners;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ShareX.UploadersLib.Json
{
    /// <summary>Source generated System.Text.Json metadata for the types ShareX.UploadersLib serializes.</summary>
    [JsonSourceGenerationOptions(IncludeFields = true)]
    // Settings and custom uploaders
    [JsonSerializable(typeof(UploadersConfig))]
    [JsonSerializable(typeof(CustomUploaderItem))]
    // OAuth
    [JsonSerializable(typeof(OAuth2Token))]
    [JsonSerializable(typeof(OAuthUserInfo))]
    // Shared
    [JsonSerializable(typeof(Dictionary<string, string>))]
    // File uploaders
    [JsonSerializable(typeof(BackblazeB2.B2Authorization))]
    [JsonSerializable(typeof(BackblazeB2.B2Error))]
    [JsonSerializable(typeof(BackblazeB2.B2Upload))]
    [JsonSerializable(typeof(BackblazeB2.B2UploadUrl))]
    [JsonSerializable(typeof(BoxFileEntry))]
    [JsonSerializable(typeof(BoxFileInfo))]
    [JsonSerializable(typeof(CopyAccountInfo))]
    [JsonSerializable(typeof(CopyContentInfo))]
    [JsonSerializable(typeof(CopyLinkRequest))]
    [JsonSerializable(typeof(CopyLinksInfo))]
    [JsonSerializable(typeof(CopyUploadInfo))]
    [JsonSerializable(typeof(DropboxAccount))]
    [JsonSerializable(typeof(DropboxLinkMetadata))]
    [JsonSerializable(typeof(DropboxListSharedLinksResult))]
    [JsonSerializable(typeof(DropboxMetadata))]
    [JsonSerializable(typeof(GoogleCloudStorage.GoogleCloudStorageMetadata))]
    [JsonSerializable(typeof(GoogleCloudStorage.GoogleCloudStorageResponse))]
    [JsonSerializable(typeof(GoogleDriveFile))]
    [JsonSerializable(typeof(GoogleDriveFileList))]
    [JsonSerializable(typeof(GoogleDriveSharedDriveList))]
    [JsonSerializable(typeof(Hostr.HostrFileUploadResponse))]
    [JsonSerializable(typeof(ImgFish.ImgFishResponse))]
    [JsonSerializable(typeof(ImmichSharedLinkResponse))]
    [JsonSerializable(typeof(ImmichUploadResponse))]
    [JsonSerializable(typeof(Lambda.LambdaResponse))]
    [JsonSerializable(typeof(LobFile.LobFileFetchAPIKeyResponse))]
    [JsonSerializable(typeof(LobFile.LobFileUploadResponse))]
    [JsonSerializable(typeof(MediaFire.GetSessionTokenResponse))]
    [JsonSerializable(typeof(MediaFire.PollUploadResponse))]
    [JsonSerializable(typeof(MediaFire.SimpleUploadResponse))]
    [JsonSerializable(typeof(MediaFire.PollUploadResponse.DoUpload), TypeInfoPropertyName = "MediaFirePollUploadDoUpload")]
    [JsonSerializable(typeof(MediaFire.SimpleUploadResponse.DoUpload), TypeInfoPropertyName = "MediaFireSimpleUploadDoUpload")]
    [JsonSerializable(typeof(OneDriveFileInfo))]
    [JsonSerializable(typeof(OneDriveFileList))]
    [JsonSerializable(typeof(OneDrivePermission))]
    [JsonSerializable(typeof(OneDriveUploadSession))]
    [JsonSerializable(typeof(OwnCloud.OwnCloudShareResponse))]
    [JsonSerializable(typeof(OwnCloud.OwnCloudShareResponseData))]
    [JsonSerializable(typeof(Pomf.PomfResponse))]
    [JsonSerializable(typeof(Pushbullet.PushbulletResponseDevices))]
    [JsonSerializable(typeof(Pushbullet.PushbulletResponseFileUpload))]
    [JsonSerializable(typeof(Pushbullet.PushbulletResponsePush))]
    [JsonSerializable(typeof(SeafileAuthResponse))]
    [JsonSerializable(typeof(SeafileCheckAccInfoResponse))]
    [JsonSerializable(typeof(SeafileDefaultLibraryObj))]
    [JsonSerializable(typeof(List<SeafileLibraryObj>))]
    [JsonSerializable(typeof(StreamableStatusResponse))]
    [JsonSerializable(typeof(StreamableTranscodeResponse))]
    [JsonSerializable(typeof(UploadMetadataRequest))]
    [JsonSerializable(typeof(UploadMetadataResponse))]
    [JsonSerializable(typeof(YouTubeVideoResponse))]
    [JsonSerializable(typeof(YouTubeVideoUpload))]
    // Image uploaders
    [JsonSerializable(typeof(Chevereto.CheveretoResponse))]
    [JsonSerializable(typeof(FlickrPhotosGetSizesResponse))]
    [JsonSerializable(typeof(ImageShackUploader.ImageShackErrorInfo))]
    [JsonSerializable(typeof(ImageShackUploader.ImageShackLoginResponse))]
    [JsonSerializable(typeof(ImageShackUploader.ImageShackUploadResult))]
    [JsonSerializable(typeof(ImgurError))]
    [JsonSerializable(typeof(ImgurErrorData))]
    [JsonSerializable(typeof(ImgurImageData))]
    [JsonSerializable(typeof(ImgurResponse))]
    [JsonSerializable(typeof(List<ImgurAlbumData>))]
    [JsonSerializable(typeof(List<ImgurImageData>))]
    [JsonSerializable(typeof(ImmioUploader.ImmioResponse))]
    [JsonSerializable(typeof(VgymeUploader.VgymeResponse))]
    // Text uploaders
    [JsonSerializable(typeof(GitHubGist.GistResponse))]
    [JsonSerializable(typeof(GitHubGist.GistUpload))]
    [JsonSerializable(typeof(Hastebin.HastebinResponse))]
    [JsonSerializable(typeof(OneTimeSecret.OneTimeSecretResponse))]
    [JsonSerializable(typeof(Paste_eeSubmitRequestBody))]
    [JsonSerializable(typeof(Paste_eeSubmitResponse))]
    [JsonSerializable(typeof(PrivateBin.Response), TypeInfoPropertyName = "PrivateBinResponse")]
    [JsonSerializable(typeof(Upaste.UpasteResponse))]
    // URL shorteners
    [JsonSerializable(typeof(BitlyURLShortener.BitlyShortenRequestBody))]
    [JsonSerializable(typeof(BitlyURLShortener.BitlyShortenResponse))]
    [JsonSerializable(typeof(FirebaseRequest))]
    [JsonSerializable(typeof(FirebaseResponse))]
    [JsonSerializable(typeof(KuttURLShortener.KuttShortenLinkBody))]
    [JsonSerializable(typeof(KuttURLShortener.KuttShortenLinkResponse))]
    [JsonSerializable(typeof(QRnetURLShortenerResponse))]
    [JsonSerializable(typeof(TwoGPURLShortenerResponse))]
    [JsonSerializable(typeof(ZeroWidthURLShortenerResponse))]
    internal sealed partial class UploadersLibJsonContext : JsonSerializerContext
    {
        // Registered with ShareXJson by the ShareX.Roslyn generated ShareXGeneratedRegistration class.
    }
}
