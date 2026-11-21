using System;
using System.Collections.Generic;
using UnityEngine;

namespace FightDataset
{
    public enum ClipKind { Unknown, Idle, Neutral, Punch, Kick, HitReaction, Block, Dodge, Stumble, Fall, GetUp }

    [Serializable]
    public sealed class CatalogClip
    {
        public string key, assetPath, displayName, classificationSource;
        public AnimationClip clip;
        public ClipKind kind;
        public bool enabled = true;
        public bool manualClassification;
        [Tooltip("Allow this fall as the final reaction only after checking its floor contact on the character models.")]
        public bool reviewedFall;
        public bool overrideImpact;
        public float durationSeconds;
        public string preferredReactionKey;
        public bool overrideTargetLandmark;
        public HumanBodyBones targetLandmark = HumanBodyBones.Head;
        [Range(0.05f, 0.95f)] public float impactNormalizedTime = 0.55f;
        public HumanBodyBones attackingLimb = HumanBodyBones.RightHand;
        public Vector2 speedRange = new Vector2(0.8f, 1.2f);
        [TextArea] public string notes;
    }

    [CreateAssetMenu(menuName = "Fight Dataset/Animation Catalog")]
    public sealed class AnimationCatalog : ScriptableObject
    {
        public List<CatalogClip> clips = new List<CatalogClip>();
        public List<GameObject> characters = new List<GameObject>();
        public string[] warnings = Array.Empty<string>();
    }
}
