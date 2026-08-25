
namespace HatagoWorks.DynamicImageCompression
{
    ﻿using UdonSharp;
    using UnityEngine;
    using VRC.SDK3.Image;
    using VRC.SDKBase;

    /// <summary>
    /// Managed result handle returned by DRCompressedImageDownloader.
    /// It intentionally mirrors the commonly used IVRCImageDownload member names,
    /// but cannot implement that interface because UdonSharp does not support it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DRCompressedImageDownload : UdonSharpBehaviour
    {
        [HideInInspector] public Texture2D Result;
        [HideInInspector] public VRCImageDownloadState State = VRCImageDownloadState.Unknown;
        [HideInInspector] public float Progress;
        [HideInInspector] public VRCImageDownloadError Error = VRCImageDownloadError.Unknown;
        [HideInInspector] public string ErrorMessage;
        [HideInInspector] public int SizeInMemoryBytes;
        [HideInInspector] public Material Material;
        [HideInInspector] public TextureInfo TextureInfo;
        [HideInInspector] public UdonSharpBehaviour UdonBehaviour;
        [HideInInspector] public VRCUrl Url;
        [HideInInspector] public string MaterialProperty;

        [HideInInspector] public bool IsCompressed;
        [HideInInspector] public bool UsedFallback;
        [HideInInspector] public string CompressionFormat;
        [HideInInspector] public string CompressionBackend;
        [HideInInspector] public string CompressionErrorCode;
        [HideInInspector] public string Phase;
        [HideInInspector] public bool IsAllocated;
        [HideInInspector] public bool IsDisposePending;
        [HideInInspector] public int RequestId;
        // OriginalWidth/Height are the dimensions the encoder treated as its
        // source (after any downscale); DownloadedWidth/Height are the native
        // download dimensions. They differ only when DownscaleDivisor > 1.
        [HideInInspector] public int OriginalWidth;
        [HideInInspector] public int OriginalHeight;
        [HideInInspector] public int DownloadedWidth;
        [HideInInspector] public int DownloadedHeight;
        [HideInInspector] public int DownscaleDivisor = 1;
        [HideInInspector] public float CompressionDurationMilliseconds;
        // Time.realtimeSinceStartup when the request was accepted (diagnostics).
        [HideInInspector] public float RequestStartedAt;
        [HideInInspector] public int ResultWidth;
        [HideInInspector] public int ResultHeight;
        [HideInInspector] public bool UsedEdgePadding;
        [HideInInspector] public bool RequiresContentUvCorrection;
        [HideInInspector] public bool MaterialUvCorrectionApplied;
        [HideInInspector] public Vector2 ContentUvScale = Vector2.one;
        [HideInInspector] public Vector2 ContentUvOffset = Vector2.zero;

        private IVRCImageDownload _nativeDownload;
        private Texture2D _ownedCompressedTexture;
        private string _materialProperty = "_MainTex";
        private DRCompressedImageDownloader _owner;
        private bool _hasSavedMaterialTransform;
        private bool _appliedMaterialTransform;
        private Vector2 _originalMaterialScale;
        private Vector2 _originalMaterialOffset;
        private Vector2 _appliedMaterialScale;
        private Vector2 _appliedMaterialOffset;
        private bool _ownsMaterialAssignment;

        public void Prepare(
            VRCUrl url,
            Material material,
            UdonSharpBehaviour receiver,
            TextureInfo textureInfo,
            string materialProperty,
            DRCompressedImageDownloader owner,
            int requestId)
        {
            Url = url;
            Material = material;
            UdonBehaviour = receiver;
            TextureInfo = textureInfo;
            _materialProperty = materialProperty == null || materialProperty == ""
                ? "_MainTex"
                : materialProperty;
            MaterialProperty = _materialProperty;

            // The Material transform is captured immediately before the result is
            // installed. This preserves changes made while the download is pending.
            _hasSavedMaterialTransform = false;
            _appliedMaterialTransform = false;
            _ownsMaterialAssignment = false;
            _originalMaterialScale = Vector2.one;
            _originalMaterialOffset = Vector2.zero;
            _appliedMaterialScale = Vector2.one;
            _appliedMaterialOffset = Vector2.zero;

            Result = null;
            State = VRCImageDownloadState.Pending;
            Progress = 0f;
            Error = VRCImageDownloadError.Unknown;
            ErrorMessage = "";
            SizeInMemoryBytes = 0;
            IsCompressed = false;
            UsedFallback = false;
            CompressionFormat = "";
            CompressionBackend = "";
            CompressionErrorCode = "";
            Phase = "Downloading";
            IsAllocated = true;
            IsDisposePending = false;
            RequestId = requestId;
            RequestStartedAt = Time.realtimeSinceStartup;
            OriginalWidth = 0;
            OriginalHeight = 0;
            DownloadedWidth = 0;
            DownloadedHeight = 0;
            DownscaleDivisor = 1;
            CompressionDurationMilliseconds = 0f;
            ResultWidth = 0;
            ResultHeight = 0;
            UsedEdgePadding = false;
            RequiresContentUvCorrection = false;
            MaterialUvCorrectionApplied = false;
            ContentUvScale = Vector2.one;
            ContentUvOffset = Vector2.zero;
            _nativeDownload = null;
            _ownedCompressedTexture = null;
            _owner = owner;
        }

        public void AttachNativeDownload(IVRCImageDownload nativeDownload)
        {
            _nativeDownload = nativeDownload;
        }

        public IVRCImageDownload GetNativeDownload()
        {
            return _nativeDownload;
        }

        public bool TargetsMaterial(Material material, string materialProperty)
        {
            return Material == material
                && MaterialProperty == materialProperty;
        }

        /// <summary>
        /// Relinquishes only this handle's Material assignment bookkeeping before a
        /// newer result is installed. The old texture remains alive and visible until
        /// the caller atomically replaces it.
        /// </summary>
        public void ReleaseMaterialAssignmentForReplacement()
        {
            if (!_ownsMaterialAssignment)
            {
                return;
            }

            RestoreMaterialTransformIfUnchanged();
            _ownsMaterialAssignment = false;
            _appliedMaterialTransform = false;
            MaterialUvCorrectionApplied = false;
        }

        public void RefreshNativeProgress()
        {
            if (_nativeDownload != null && State == VRCImageDownloadState.Pending)
            {
                Progress = _nativeDownload.Progress;
            }
        }

        public void CompleteCompressed(
            Texture2D texture,
            string format,
            string backend,
            int byteCount,
            int sourceWidth,
            int sourceHeight,
            int downloadedWidth,
            int downloadedHeight,
            int downscaleDivisor,
            float compressionDurationMilliseconds)
        {
            DownloadedWidth = downloadedWidth;
            DownloadedHeight = downloadedHeight;
            DownscaleDivisor = downscaleDivisor < 1 ? 1 : downscaleDivisor;
            CompressionDurationMilliseconds = compressionDurationMilliseconds;
            _ownedCompressedTexture = texture;
            Result = texture;
            IsCompressed = true;
            UsedFallback = false;
            CompressionFormat = format;
            CompressionBackend = backend;
            CompressionErrorCode = "";
            SizeInMemoryBytes = byteCount;
            Error = VRCImageDownloadError.Unknown;
            ErrorMessage = "";
            State = VRCImageDownloadState.Complete;
            Progress = 1f;
            Phase = "Complete";
            OriginalWidth = sourceWidth > 0 ? sourceWidth : texture.width;
            OriginalHeight = sourceHeight > 0 ? sourceHeight : texture.height;
            ResultWidth = texture.width;
            ResultHeight = texture.height;
            UsedEdgePadding = OriginalWidth != ResultWidth || OriginalHeight != ResultHeight;
            RequiresContentUvCorrection = UsedEdgePadding;
            ContentUvScale = new Vector2(
                (float)OriginalWidth / ResultWidth,
                (float)OriginalHeight / ResultHeight);
            ContentUvOffset = Vector2.zero;

            ApplyResultToMaterial();
            DisposeNativeDownload();
        }

        public void CompleteFallback(string errorCode)
        {
            if (_nativeDownload == null || _nativeDownload.Result == null)
            {
                CompleteError(VRCImageDownloadError.Unknown, "FallbackSourceMissing", errorCode);
                return;
            }

            Result = _nativeDownload.Result;
            IsCompressed = false;
            UsedFallback = true;
            CompressionFormat = "Original";
            CompressionBackend = "UncompressedFallback";
            CompressionErrorCode = errorCode;
            SizeInMemoryBytes = _nativeDownload.SizeInMemoryBytes;
            Error = VRCImageDownloadError.Unknown;
            ErrorMessage = "";
            State = VRCImageDownloadState.Complete;
            Progress = 1f;
            Phase = "Complete";
            OriginalWidth = Result.width;
            OriginalHeight = Result.height;
            DownloadedWidth = Result.width;
            DownloadedHeight = Result.height;
            DownscaleDivisor = 1;
            ResultWidth = Result.width;
            ResultHeight = Result.height;
            UsedEdgePadding = false;
            RequiresContentUvCorrection = false;
            ContentUvScale = Vector2.one;
            ContentUvOffset = Vector2.zero;

            ApplyResultToMaterial();
            // The native handle remains alive because it owns Result.
        }

        public void CompleteError(
            VRCImageDownloadError error,
            string message,
            string compressionErrorCode)
        {
            Error = error;
            ErrorMessage = message == null ? "" : message;
            CompressionErrorCode = compressionErrorCode == null ? "" : compressionErrorCode;
            State = VRCImageDownloadState.Error;
            Progress = 1f;
            Phase = "Error";
            DisposeNativeDownload();
        }

        public void DisposeNativeDownload()
        {
            if (_nativeDownload != null)
            {
                _nativeDownload.Dispose();
                _nativeDownload = null;
            }
        }

        private void RequestDispose()
        {
            if (!IsAllocated && State == VRCImageDownloadState.Unloaded)
            {
                return;
            }

            if (_owner != null && _owner.RequestHandleDispose(this))
            {
                DetachOwnedMaterialTexture();
                Result = null;
                State = VRCImageDownloadState.Unloaded;
                Progress = 0f;
                SizeInMemoryBytes = 0;
                Phase = "DisposePending";
                IsDisposePending = true;
                return;
            }

            DisposeImmediately();
        }

        /// <summary>
        /// Releases this pooled handle only while it still represents the saved
        /// request generation. Callers must save RequestId with the returned
        /// handle; a zero-argument Dispose cannot be made safe across pool reuse.
        /// </summary>
        public bool DisposeIfCurrent(int expectedRequestId)
        {
            if (!IsAllocated || RequestId != expectedRequestId)
            {
                return false;
            }

            RequestDispose();
            return true;
        }

        public void CompleteDeferredDispose()
        {
            _owner = null;
            DisposeImmediately();
        }

        public void DisposeFromService()
        {
            _owner = null;
            DisposeImmediately();
        }

        private void DisposeImmediately()
        {

            DetachOwnedMaterialTexture();
            DisposeNativeDownload();

            if (_ownedCompressedTexture != null)
            {
                UnityEngine.Object.Destroy(_ownedCompressedTexture);
                _ownedCompressedTexture = null;
            }

            Result = null;
            State = VRCImageDownloadState.Unloaded;
            Progress = 0f;
            SizeInMemoryBytes = 0;
            IsCompressed = false;
            UsedFallback = false;
            CompressionFormat = "";
            CompressionBackend = "";
            CompressionErrorCode = "";
            Phase = "Unloaded";
            IsAllocated = false;
            IsDisposePending = false;
            RequestId = 0;
            OriginalWidth = 0;
            OriginalHeight = 0;
            DownloadedWidth = 0;
            DownloadedHeight = 0;
            DownscaleDivisor = 1;
            CompressionDurationMilliseconds = 0f;
            ResultWidth = 0;
            ResultHeight = 0;
            UsedEdgePadding = false;
            RequiresContentUvCorrection = false;
            MaterialUvCorrectionApplied = false;
            ContentUvScale = Vector2.one;
            ContentUvOffset = Vector2.zero;
            Material = null;
            MaterialProperty = "";
            TextureInfo = null;
            UdonBehaviour = null;
            Url = null;
            _owner = null;
            _hasSavedMaterialTransform = false;
            _appliedMaterialTransform = false;
            _originalMaterialScale = Vector2.one;
            _originalMaterialOffset = Vector2.zero;
            _appliedMaterialScale = Vector2.one;
            _appliedMaterialOffset = Vector2.zero;
            _ownsMaterialAssignment = false;
        }

        private void OnDestroy()
        {
            DisposeFromService();
        }

        private void ApplyResultToMaterial()
        {
            if (Material != null
                && Result != null
                && _materialProperty != null
                && _materialProperty != ""
                && Material.HasProperty(_materialProperty))
            {
                if (_owner != null)
                {
                    _owner.ReleasePriorMaterialAssignments(this);
                }

                _hasSavedMaterialTransform = true;
                _originalMaterialScale = Material.GetTextureScale(_materialProperty);
                _originalMaterialOffset = Material.GetTextureOffset(_materialProperty);
                Material.SetTexture(_materialProperty, Result);
                _ownsMaterialAssignment = true;

                if (UsedEdgePadding && _hasSavedMaterialTransform)
                {
                    _appliedMaterialScale = new Vector2(
                        _originalMaterialScale.x * ContentUvScale.x,
                        _originalMaterialScale.y * ContentUvScale.y);
                    _appliedMaterialOffset = new Vector2(
                        _originalMaterialOffset.x * ContentUvScale.x + ContentUvOffset.x,
                        _originalMaterialOffset.y * ContentUvScale.y + ContentUvOffset.y);
                    Material.SetTextureScale(_materialProperty, _appliedMaterialScale);
                    Material.SetTextureOffset(_materialProperty, _appliedMaterialOffset);
                    _appliedMaterialTransform = true;
                    MaterialUvCorrectionApplied = true;
                }
            }
        }

        private void DetachOwnedMaterialTexture()
        {
            if (Material == null
                || _materialProperty == null
                || _materialProperty == ""
                || !Material.HasProperty(_materialProperty))
            {
                _appliedMaterialTransform = false;
                return;
            }

            bool ownsMaterialTexture = _ownsMaterialAssignment
                && Result != null
                && Material.GetTexture(_materialProperty) == Result;
            if (ownsMaterialTexture)
            {
                Material.SetTexture(_materialProperty, null);
            }

            RestoreMaterialTransformIfUnchanged();
            _ownsMaterialAssignment = false;
            _appliedMaterialTransform = false;
            MaterialUvCorrectionApplied = false;
        }

        private void RestoreMaterialTransformIfUnchanged()
        {
            if (!_appliedMaterialTransform
                || !_hasSavedMaterialTransform
                || Material == null
                || _materialProperty == null
                || _materialProperty == ""
                || !Material.HasProperty(_materialProperty))
            {
                return;
            }

            // Restore each component independently so caller changes to one do not
            // prevent cleanup of the other wrapper-owned component.
            if (Material.GetTextureScale(_materialProperty) == _appliedMaterialScale)
            {
                Material.SetTextureScale(_materialProperty, _originalMaterialScale);
            }
            if (Material.GetTextureOffset(_materialProperty) == _appliedMaterialOffset)
            {
                Material.SetTextureOffset(_materialProperty, _originalMaterialOffset);
            }
        }
    }
}
