// #define LFE_DEBUG

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Math = UnityEngine.Mathf;

namespace LFE
{
    public class AutoPupil : MVRScript
    {
        public float PupilNeutralValue;
        public JSONStorableFloat DarkAdjustSpeedStorable;
        public JSONStorableFloat LightAdjustSpeedStorable;
        public JSONStorableFloat IdleMaxDelayStorable;
        public JSONStorableFloat IdleStrengthStorable;
        public JSONStorableFloat IdleAdjustSpeedStorable;

        private bool _initCompleted = false;
        private DAZMeshEyelidControl _autoBlinker;
        private DAZMorph _pupilMorph;
        private DAZMorph _eyesClosedLeftMorph;
        private DAZMorph _eyesClosedRightMorph;
        private BrightnessDetector _detector;
        private JSONStorableString _screenAtomUid; // so that when scene loads up this special atom is remembered
        private int _layerMask;
        private float _idleCountDown = 0;
        private float _idleSign = 1;
        private float _lastBrightness;
        private EyeDialationAnimation _currentAnimation = null;

        public void InitFields(Atom atom)
        {
            _initCompleted = false;

            var morphControlUI = (atom.GetStorableByID("geometry") as DAZCharacterSelector).morphsControlUI;

            // different morph for male and female
            foreach (var pupilMorphName in new List<string> { "Pupils Dialate", "Pupils Dilate" })
            {
                _pupilMorph = morphControlUI.GetMorphByDisplayName(pupilMorphName);
                if (_pupilMorph != null)
                {
                    break;
                }
            }

            _eyesClosedLeftMorph = morphControlUI.GetMorphByDisplayName("Eyes Closed Left");
            _eyesClosedRightMorph = morphControlUI.GetMorphByDisplayName("Eyes Closed Right");

            _autoBlinker = atom.GetComponentInChildren<DAZMeshEyelidControl>();

            _pupilMorph.morphValue = 0;
            PupilNeutralValue = _pupilMorph.morphValue;

        }

        public void InitUserInterface()
        {
            _initCompleted = false;

            LightAdjustSpeedStorable = new JSONStorableFloat("Light Adjust Within", 3.50f, 0f, 10f);
            CreateSlider(LightAdjustSpeedStorable);
            RegisterFloat(LightAdjustSpeedStorable);

            DarkAdjustSpeedStorable = new JSONStorableFloat("Dark Adjust Within", 5.00f, 0f, 10f);
            CreateSlider(DarkAdjustSpeedStorable);
            RegisterFloat(DarkAdjustSpeedStorable);

            IdleAdjustSpeedStorable = new JSONStorableFloat("Idle: Adjust Over Seconds", 1.00f, 0f, 10f);
            CreateSlider(IdleAdjustSpeedStorable, rightSide: true);
            RegisterFloat(IdleAdjustSpeedStorable);

            IdleStrengthStorable = new JSONStorableFloat("Idle: Strength", 0.05f, 0f, 1f);
            CreateSlider(IdleStrengthStorable, rightSide: true);
            RegisterFloat(IdleStrengthStorable);

            IdleMaxDelayStorable = new JSONStorableFloat("Idle: Next Random Run", 2.50f, 0f, 10f);
            CreateSlider(IdleMaxDelayStorable, rightSide: true);
            RegisterFloat(IdleMaxDelayStorable);

            _screenAtomUid = new JSONStorableString("DetectorAtomUid", GenerateAtomName("LightDetector", 5)); // save this so we can restore it
        }

        public override void Init()
        {
            if (containingAtom.type != "Person")
            {
                SuperController.LogError($"This plugin needs to be put on a 'Person' atom only, not a '{containingAtom.type}'");
                return;
            }

            StartCoroutine(InitCoroutine());
        }

        private IEnumerator InitCoroutine()
        {

            InitFields(containingAtom);
            InitUserInterface();

            // find any unused layer mask and just pick that
            _layerMask = Enumerable.Range(0, 31).FirstOrDefault(i => LayerMask.LayerToName(i).Equals(""));
#if LFE_DEBUG
            for(var i = 0; i < 32; i++) {
                SuperController.LogMessage($"layer {i}: {LayerMask.LayerToName(i)}");
            }
#endif

            var sc = SuperController.singleton;
            var head = containingAtom.rigidbodies.FirstOrDefault(rb => rb.name.Equals("head"));
            var screen = sc.GetAtomByUid(_screenAtomUid.val);

            // create the screen that the camera will be looking at for light colors
            // place it on a special layer so we can show just this later on
            if (screen == null)
            {
                yield return sc.AddAtomByType("ImagePanel", useuid: _screenAtomUid.val);
                screen = sc.GetAtomByUid(_screenAtomUid.val);
            }
            var imageObject = screen.transform.Find("reParentObject/object");
            var detectorOffset = (Vector3.up * 0.06f) + (Vector3.forward * 0.11f);
            var detectorCameraOffset = detectorOffset + (Vector3.forward * 0.05f);

            screen.hidden = true;
            foreach (var t in ChildTransforms(imageObject))
            {
                t.gameObject.layer = _layerMask;
            }

            foreach (var r in imageObject.GetComponentsInChildren<Renderer>())
            {
                r.material.color = Color.black;
            }

            var mainControl = screen.freeControllers[0];
            mainControl.transform.parent = head.transform;
            mainControl.transform.localPosition = detectorOffset;
            mainControl.currentRotationState = FreeControllerV3.RotationState.Off;
            mainControl.currentPositionState = FreeControllerV3.PositionState.Off;
            imageObject.localScale = new Vector3(0.105f, 0.08f, 0.05f);


            // create the camera that reads the light
            _detector = gameObject.AddComponent<BrightnessDetector>();
            _detector.Detector.CopyFrom(CameraTarget.centerTarget.targetCamera);
            _detector.Detector.transform.parent = head.transform;
            _detector.Detector.transform.localPosition = detectorCameraOffset;
            _detector.Detector.depth = CameraTarget.centerTarget.targetCamera.depth - 1;
            _detector.Detector.cullingMask |= 1 << _layerMask;

            // hide the screen created above from all cameras except out _detector
            foreach (var camera in Camera.allCameras)
            {
                if (camera.tag != _detector.Detector.tag)
                {
                    camera.cullingMask &= ~(1 << _layerMask);
                }
            }
#if LFE_DEBUG
            // enable the visibility of the screen in debug
            foreach(var camera in Camera.allCameras) {
                camera.cullingMask |= 1 << _layerMask;
            }
#endif

            _initCompleted = true;
        }

        public void OnDestroy()
        {
            if (_pupilMorph != null)
            {
                _pupilMorph.morphValue = PupilNeutralValue;
            }

            // restore built in camera cullingMasks
            foreach (var camera in Camera.allCameras)
            {
                if (camera.tag != _detector.Detector.tag)
                {
                    camera.cullingMask |= 1 << _layerMask;
                }
            }

            // remove the light detection atom
            if (!string.IsNullOrEmpty(_screenAtomUid.val))
            {
                var atom = SuperController.singleton.GetAtomByUid(_screenAtomUid.val);
                if (atom != null)
                {
                    SuperController.singleton.RemoveAtom(atom);
                }
            }

            Destroy(_detector);
        }

        void OnEnable()
        {
            _detector.gameObject.SetActive(true);
        }

        void OnDisable()
        {
            _detector.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (!_initCompleted) { return; }
            if (SuperController.singleton.freezeAnimation) { return; }

            try
            {
                // run the scheduled animation
                if (_currentAnimation != null)
                {
                    _pupilMorph.morphValueAdjustLimits = Math.Clamp(_currentAnimation.Update(), -1.5f, 2.0f);
                    if (_currentAnimation.IsFinished)
                    {
                        _currentAnimation = null;

                        // schedule a new idle
                        _idleCountDown = UnityEngine.Random.Range(0.01f, Math.Max(IdleMaxDelayStorable.val, 0.01f));
                    }
                }

                var brightness = CalculateBrightness();
                var currentValue = _pupilMorph.morphValue;
                var targetValue = BrightnessToMorphValue(brightness);
                var duration = 0f;

                if (_lastBrightness == brightness)
                {
                    // maybe schedule an idle animation - but do not interrupt an animation in progress just for idle
                    if (_currentAnimation == null && _idleCountDown < 0)
                    {
                        duration = Math.Max(IdleAdjustSpeedStorable.val, 0.01f);
                        _idleSign = _idleSign * -1;
                        targetValue = targetValue + (_idleSign * UnityEngine.Random.Range(0.01f, IdleStrengthStorable.val));

                        _currentAnimation = new EyeDialationAnimation(currentValue, targetValue, duration, (p) => Easings.BackEaseOut(p));
                    }
                    else
                    {
                        _idleCountDown -= Time.deltaTime;
                    }
                }
                else
                {
                    // schedule brightness adjustment animation - override any currently running animation for this one. light always wins
                    duration = targetValue > currentValue
                        ? DarkAdjustSpeedStorable.val
                        : LightAdjustSpeedStorable.val;
                    _currentAnimation = new EyeDialationAnimation(currentValue, targetValue, duration, (p) => Easings.ElasticEaseOut(p));
                }

                _lastBrightness = brightness;
            }
            catch (Exception ex)
            {
                SuperController.LogError(ex.ToString());
            }
        }

        private float CalculateBrightness()
        {
            // calculate brightness of lights
            var defaultBright = _detector.DetectedBrightness;

            // calculate any shade from blinking
            // TODO: see if is there any way to just measure the distance between top and bottom eyelid to make this more universal
            var blinkDimming = 1f;
            if (_autoBlinker?.currentWeight > 0.35)
            {
                blinkDimming = Math.SmoothStep(1, 0.25f, _autoBlinker.currentWeight);
            }
            else if (_eyesClosedLeftMorph?.morphValue > 0.35)
            {
                blinkDimming = Math.SmoothStep(1, 0.25f, _eyesClosedLeftMorph.morphValue);
            }

            return defaultBright * blinkDimming;
        }

        private float BrightnessToMorphValue(float brightness)
        {
            return -1 * Math.Clamp(PupilNeutralValue + Math.Lerp(-1.0f, 1.5f, brightness), -1.0f, 1.5f);
        }

        private string GenerateAtomName(string prefix, int length)
        {
            var random = new System.Random();
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

            var shortRand = new string(Enumerable.Repeat(chars, length).Select(s => s[random.Next(s.Length)]).ToArray());
            return $"{prefix}-{shortRand}";
        }

        private IEnumerable<Transform> ChildTransforms(Transform parent, int level = 0, string path = "")
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                foreach (var c in ChildTransforms(child, level + 1, $"{path}/{child.name}"))
                {
                    yield return c;
                }
#if LFE_DEBUG
                SuperController.LogMessage($"xfm {path}/{child.name}");
#endif
                yield return child;
            }
            yield break;
        }
    }

    public class EyeDialationAnimation
    {
        private float _startValue;
        private float _targetValue;
        private Func<float, float> _easing;
        private float _duration;
        private float _currentTime;

        public bool IsFinished => _currentTime >= _duration;

        public EyeDialationAnimation(float startValue, float targetValue, float duration, Func<float, float> easing = null)
        {
            _startValue = startValue;
            _targetValue = targetValue;
            _easing = easing ?? ((p) => Easings.Linear(p));
            _duration = duration;
            _currentTime = 0;
        }

        public float Update()
        {
            _currentTime = _currentTime + Time.deltaTime;
            if (_currentTime > _duration)
            {
                _currentTime = _duration;
            }

            // how far into the time are we?
            var percentageComplete = Math.InverseLerp(0, _duration, _currentTime);

            // return a new morph value that represents progressing _currentTime thorugh things
            return _startValue + ((_targetValue - _startValue) * _easing(percentageComplete));
        }
    }

    // https://github.com/acron0/Easings for more
    static public class Easings
    {
        /// <summary>
        /// Constant Pi.
        /// </summary>
        private const float PI = Math.PI;

        /// <summary>
        /// Constant Pi / 2.
        /// </summary>
        private const float HALFPI = Math.PI / 2.0f;

        /// <summary>
        /// Modeled after the line y = x
        /// </summary>
        static public float Linear(float p)
        {
            return p;
        }

        /// <summary>
        /// Modeled after the damped sine wave y = sin(-13pi/2*(x + 1))*Math.Pow(2, -10x) + 1
        /// </summary>
        static public float ElasticEaseOut(float p)
        {
            return Math.Sin(-13 * HALFPI * (p + 1)) * Math.Pow(2, -10 * p) + 1;
        }

        /// <summary>
        /// Modeled after overshooting cubic y = 1-((1-x)^3-(1-x)*sin((1-x)*pi))
        /// </summary>
        static public float BackEaseOut(float p)
        {
            float f = (1 - p);
            return 1 - (f * f * f - f * Math.Sin(f * PI));
        }
    }

    public class BrightnessDetector : MonoBehaviour
    {
        private const int CAMERA_IMG_WITDH = 128;
        private const int CAMERA_IMG_HEIGHT = 128;

        public Camera Detector { get; private set; }
        public float DetectedBrightness { get; private set; }
        public Color DetectedColor { get; private set; }
        public float PollFrequency { get; private set; }

        private float pollCountdown;
        private RenderTexture cameraRenderTexture;
        private Texture2D cameraTexture2d;

        void Awake()
        {
            pollCountdown = 0;
            cameraRenderTexture = null;
            cameraTexture2d = new Texture2D(CAMERA_IMG_WITDH, CAMERA_IMG_HEIGHT);

            PollFrequency = 0.1f;
            DetectedBrightness = 0;
            DetectedColor = Color.black;

            Transform cameraHolder = new GameObject("CameraHolder").transform;
            cameraHolder.parent = transform;

            Detector = cameraHolder.gameObject.AddComponent<Camera>();
            Detector.transform.localPosition = Vector3.forward;
            Detector.name = "BrightnessDetector";
            Detector.tag = "BrightnessDetector";
            Detector.clearFlags = CameraClearFlags.SolidColor;
            Detector.backgroundColor = Color.black;
        }

        void Update()
        {
            if (cameraRenderTexture != null)
            {
                // get a copy of the pixels into a Texture2D
                if (cameraTexture2d.width != cameraRenderTexture.width || cameraTexture2d.height != cameraRenderTexture.height)
                {
                    cameraTexture2d.Resize(cameraRenderTexture.width, cameraRenderTexture.height);
                }

                var previous = RenderTexture.active;
                RenderTexture.active = cameraRenderTexture;
                cameraTexture2d.ReadPixels(new Rect(0, 0, cameraRenderTexture.width, cameraRenderTexture.height), 0, 0);
                cameraTexture2d.Apply();
                RenderTexture.active = previous;

                // average the colors in the image
                var colors = cameraTexture2d.GetPixels32();
                var total = colors.Length;
                var r = 0; var g = 0; var b = 0; var a = 0;
                for (int i = 0; i < total; i++)
                {
                    r += colors[i].r;
                    g += colors[i].g;
                    b += colors[i].b;
                    a += colors[i].a;
                }
                var color = new Color(r / total, g / total, b / total, a / total);
                // https://stackoverflow.com/questions/596216/formula-to-determine-brightness-of-rgb-color
                var brightness = Math.Sqrt(
                    color.r * color.r * .299f +
                    color.g * color.g * .587f +
                    color.b * color.b * .114f
                ) / 255;
                if (brightness > 1) { brightness = 1; }
                else if (brightness < 0) { brightness = 0; }

                // set our public properties now
                DetectedColor = color;
                DetectedBrightness = brightness;
#if LFE_DEBUG
                SuperController.LogMessage($"color = {color} brightness = {brightness}");
#endif

                // stop capturing the screen
                Detector.depth -= 2;
                Detector.targetTexture = null;
                RenderTexture.ReleaseTemporary(cameraRenderTexture);
                cameraRenderTexture = null;
            }

            pollCountdown -= Time.deltaTime;
            if (pollCountdown > 0)
            {
                // wait
                return;
            }
            else
            {
                // schedule the next update to capture the screen
                pollCountdown = PollFrequency;
                cameraRenderTexture = RenderTexture.GetTemporary(CAMERA_IMG_WITDH, CAMERA_IMG_HEIGHT, 16);
                Detector.targetTexture = cameraRenderTexture;
                Detector.depth += 2;
            }
        }

        void OnDestroy()
        {
            if (cameraRenderTexture != null)
            {
                Detector.targetTexture = null;
                RenderTexture.ReleaseTemporary(cameraRenderTexture);
                cameraRenderTexture = null;
            }
            Destroy(Detector);
            if (cameraTexture2d != null)
            {
                Destroy(cameraTexture2d);
            }
        }
    }
}
