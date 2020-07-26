using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Math = UnityEngine.Mathf;


namespace LFE
{

    public class AutoPupil : MVRScript
    {

        public DAZMeshEyelidControl AutoBlinker;

        private DAZBone LeftEye;
        private DAZBone RightEye;
        private FreeControllerV3 HeadControl;

        private DAZMorph PupilMorph;
        private DAZMorph EyesClosedLeftMorph;
        private DAZMorph EyesClosedRightMorph;
        private Light[] SceneLights;

        public float PupilNeutralValue;
        public JSONStorableFloat DarkAdjustSpeedStorable;
        public JSONStorableFloat LightAdjustSpeedStorable;
        public JSONStorableFloat IdleMaxDelayStorable;
        public JSONStorableFloat IdleStrengthStorable;
        public JSONStorableFloat IdleAdjustSpeedStorable;
        public bool InitCompleted = false;

        private Vector3 CenterEyePosition => (LeftEye.transform.position + RightEye.transform.position) / 2;

        public void InitFields(Atom atom) {
            InitCompleted = false;

            var morphControlUI = (atom.GetStorableByID("geometry") as DAZCharacterSelector).morphsControlUI;

            // different morph for male and female
            foreach (var pupilMorphName in new List<string> { "Pupils Dialate", "Pupils Dilate" })
            {
                PupilMorph = morphControlUI.GetMorphByDisplayName(pupilMorphName);
                if (PupilMorph != null)
                {
                    break;
                }
            }

            EyesClosedLeftMorph = morphControlUI.GetMorphByDisplayName("Eyes Closed Left");
            EyesClosedRightMorph = morphControlUI.GetMorphByDisplayName("Eyes Closed Right");

            LeftEye = atom.GetStorableByID("lEye") as DAZBone;
            RightEye = atom.GetStorableByID("rEye") as DAZBone;

            HeadControl = atom.freeControllers.FirstOrDefault(c => c.name.Equals("headControl"));

            SceneLights = atom.transform.root.GetComponentsInChildren<Light>();

            AutoBlinker = atom.GetComponentInChildren<DAZMeshEyelidControl>();

            PupilMorph.morphValue = 0;
            PupilNeutralValue = PupilMorph.morphValue;
        }

        public void InitUserInterface() {
            InitCompleted = false;

            LightAdjustSpeedStorable = new JSONStorableFloat("Light Adjust Within", 2.00f, 0f, 10f);
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
        }


        public override void Init()
        {
            if (containingAtom.type != "Person")
            {
                SuperController.LogError($"This plugin needs to be put on a 'Person' atom only, not a '{containingAtom.type}'");
                return;
            }

            InitFields(containingAtom);
            InitUserInterface();

            SuperController.singleton.onAtomUIDsChangedHandlers += (atomUids) => InitFields(containingAtom);
            SuperController.singleton.onAtomUIDRenameHandlers += (oldName, newName) => InitFields(containingAtom);

            InitCompleted = true;

        }

        public void OnDestroy()
        {
            if (PupilMorph != null)
            {
                PupilMorph.morphValue = PupilNeutralValue;
            }
        }

        float idleCountDown = 0;
        float idleSign = 1;
        float lastBrightness;

        EyeDialationAnimation currentAnimation = null;

        private void Update()
        {

            // SuperController.singleton.ClearMessages();

            if (!InitCompleted) { return; }
            if (SuperController.singleton.freezeAnimation) { return; }

            try
            {
                // run the scheduled animation
                if (currentAnimation != null)
                {
                    PupilMorph.morphValueAdjustLimits = Math.Clamp(currentAnimation.Update(), -1.5f, 2.0f);
                    if (currentAnimation.IsFinished)
                    {
                        currentAnimation = null;

                        // schedule a new idle
                        idleCountDown = UnityEngine.Random.Range(0.01f, Math.Max(IdleMaxDelayStorable.val, 0.01f));
                    }
                }

                var brightness = CalculateBrightness();
                var currentValue = PupilMorph.morphValue;
                var targetValue = BrightnessToMorphValue(brightness);
                var duration = 0f;

                if (lastBrightness == brightness)
                {
                    // maybe schedule an idle animation - but do not interrupt an animation in progress just for idle
                    if (currentAnimation == null && idleCountDown < 0)
                    {
                        duration = Math.Max(IdleAdjustSpeedStorable.val, 0.01f);
                        idleSign = idleSign * -1;
                        targetValue = targetValue + (idleSign * UnityEngine.Random.Range(0.01f, IdleStrengthStorable.val));

                        currentAnimation = new EyeDialationAnimation(currentValue, targetValue, duration, (p) => Easings.BackEaseOut(p));
                    }
                    else
                    {
                        idleCountDown -= Time.deltaTime;
                    }
                }
                else
                {
                    // schedule brightness adjustment animation - override any currently running animation for this one. light always wins
                    duration = targetValue > currentValue
                        ? DarkAdjustSpeedStorable.val
                        : LightAdjustSpeedStorable.val;
                    currentAnimation = new EyeDialationAnimation(currentValue, targetValue, duration, (p) => Easings.ElasticEaseOut(p));
                }

                lastBrightness = brightness;
            }
            catch (Exception ex)
            {
                SuperController.LogError(ex.ToString());
            }
        }

        private float CalculateBrightness()
        {
            // calculate brightness of lights
            var defaultBright = BrightnessOfLightsOnEyes();

            // calculate any shade from blinking
            var blinkDimming = 1f;
            if (AutoBlinker?.currentWeight > 0.35)
            {
                blinkDimming = Math.SmoothStep(1, 0.25f, AutoBlinker.currentWeight);
            }
            else if (EyesClosedLeftMorph?.morphValue > 0.35)
            {
                blinkDimming = Math.SmoothStep(1, 0.25f, EyesClosedLeftMorph.morphValue);
            }

            return defaultBright * blinkDimming;
        }

        private float BrightnessToMorphValue(float brightness)
        {
            return -1 * Math.Clamp(PupilNeutralValue + Math.Lerp(-1.0f, 1.5f, brightness), -1.0f, 1.5f);
        }

        private IEnumerable<Light> GetRelevantLights()
        {
            var eyePosition = CenterEyePosition; // slight cost to calculate over and over in the loop
            foreach (var light in SceneLights)
            {
                if (light == null)
                {
                    continue;
                }

                if (!light.isActiveAndEnabled)
                {
                    // SuperController.LogMessage($"{light} not active");
                    continue;
                }

                if (light.type == LightType.Area)
                {
                    // ignore these lights
                    continue;
                }

                if (light.type == LightType.Spot || light.type == LightType.Point)
                {
                    if (Vector3.Distance(eyePosition, light.transform.position) > light.range)
                    {
                        // SuperController.LogMessage($"{light} out of range");
                        continue;
                    }
                }


                // is the light in front of the containing atom?
                // note: this is the dotproduct which is cosine of angle between the two vectors
                var headingDot = Vector3.Dot(HeadControl.transform.position - light.transform.position, HeadControl.transform.forward);
                if (headingDot > 0)
                {
                    continue;
                }

                if (light.type == LightType.Spot)
                {
                    const float angleFudgeFactor = 1.1f;
                    var angle = Vector3.Angle(eyePosition - light.transform.position, light.transform.forward) * angleFudgeFactor;
                    // SuperController.LogMessage($"angle = {angle} spotAngle = {light.spotAngle}");
                    if (angle * 2 > light.spotAngle)
                    {
                        // SuperController.LogMessage($"{light} angle {light.spotAngle} out of range {angle}");
                        continue;
                    }
                }

                yield return light;
            }
        }

        // https://www.nbdtech.com/Blog/archive/2008/04/27/Calculating-the-Perceived-Brightness-of-a-Color.aspx
        // return a number between 0 and 1 inclusive
        private float PerceivedIntensity(Light light, Vector3 toTarget)
        {
            const int MAX_INTENSITY = 8;

            var intensity = 0f;
            if (light.type == LightType.Spot || light.type == LightType.Point)
            {
                var distance = Vector3.Distance(toTarget, light.transform.position);
                var distanceProportion = Math.InverseLerp(light.range, 0, distance);

                intensity = light.intensity * Easings.QuadraticEaseOut(distanceProportion);
            }
            else if (light.type == LightType.Area)
            {
                intensity = 0f;
            }
            else
            {
                intensity = light.intensity;
            }

            var color = light.color * intensity;

            // https://stackoverflow.com/questions/596216/formula-to-determine-brightness-of-rgb-color
            return Math.Sqrt(
                color.r * color.r * .299f +
                color.g * color.g * .587f +
                color.b * color.b * .114f) / MAX_INTENSITY;
        }

        private float BrightnessOfLightsOnEyes()
        {
            var maximum = 0f;
            var eyePosition = CenterEyePosition;
            foreach (var light in GetRelevantLights())
            {
                var brightness = PerceivedIntensity(light, eyePosition);
                if (brightness > maximum)
                {
                    maximum = brightness;
                }
            }
            return Math.Lerp(0, 1, maximum * 3f);
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

    // https://github.com/acron0/Easings
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
        /// Modeled after the parabola y = x^2
        /// </summary>
        static public float QuadraticEaseIn(float p)
        {
            return p * p;
        }

        /// <summary>
        /// Modeled after the parabola y = -x^2 + 2x
        /// </summary>
        static public float QuadraticEaseOut(float p)
        {
            return -(p * (p - 2));
        }

        /// <summary>
        /// Modeled after the piecewise quadratic
        /// y = (1/2)((2x)^2)             ; [0, 0.5)
        /// y = -(1/2)((2x-1)*(2x-3) - 1) ; [0.5, 1]
        /// </summary>
        static public float QuadraticEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 2 * p * p;
            }
            else
            {
                return (-2 * p * p) + (4 * p) - 1;
            }
        }

        /// <summary>
        /// Modeled after the cubic y = x^3
        /// </summary>
        static public float CubicEaseIn(float p)
        {
            return p * p * p;
        }

        /// <summary>
        /// Modeled after the cubic y = (x - 1)^3 + 1
        /// </summary>
        static public float CubicEaseOut(float p)
        {
            float f = (p - 1);
            return f * f * f + 1;
        }

        /// <summary>
        /// Modeled after the piecewise cubic
        /// y = (1/2)((2x)^3)       ; [0, 0.5)
        /// y = (1/2)((2x-2)^3 + 2) ; [0.5, 1]
        /// </summary>
        static public float CubicEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 4 * p * p * p;
            }
            else
            {
                float f = ((2 * p) - 2);
                return 0.5f * f * f * f + 1;
            }
        }

        /// <summary>
        /// Modeled after the quartic x^4
        /// </summary>
        static public float QuarticEaseIn(float p)
        {
            return p * p * p * p;
        }

        /// <summary>
        /// Modeled after the quartic y = 1 - (x - 1)^4
        /// </summary>
        static public float QuarticEaseOut(float p)
        {
            float f = (p - 1);
            return f * f * f * (1 - p) + 1;
        }

        /// <summary>
        // Modeled after the piecewise quartic
        // y = (1/2)((2x)^4)        ; [0, 0.5)
        // y = -(1/2)((2x-2)^4 - 2) ; [0.5, 1]
        /// </summary>
        static public float QuarticEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 8 * p * p * p * p;
            }
            else
            {
                float f = (p - 1);
                return -8 * f * f * f * f + 1;
            }
        }

        /// <summary>
        /// Modeled after the quintic y = x^5
        /// </summary>
        static public float QuinticEaseIn(float p)
        {
            return p * p * p * p * p;
        }

        /// <summary>
        /// Modeled after the quintic y = (x - 1)^5 + 1
        /// </summary>
        static public float QuinticEaseOut(float p)
        {
            float f = (p - 1);
            return f * f * f * f * f + 1;
        }

        /// <summary>
        /// Modeled after the piecewise quintic
        /// y = (1/2)((2x)^5)       ; [0, 0.5)
        /// y = (1/2)((2x-2)^5 + 2) ; [0.5, 1]
        /// </summary>
        static public float QuinticEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 16 * p * p * p * p * p;
            }
            else
            {
                float f = ((2 * p) - 2);
                return 0.5f * f * f * f * f * f + 1;
            }
        }

        /// <summary>
        /// Modeled after quarter-cycle of sine wave
        /// </summary>
        static public float SineEaseIn(float p)
        {
            return Math.Sin((p - 1) * HALFPI) + 1;
        }

        /// <summary>
        /// Modeled after quarter-cycle of sine wave (different phase)
        /// </summary>
        static public float SineEaseOut(float p)
        {
            return Math.Sin(p * HALFPI);
        }

        /// <summary>
        /// Modeled after half sine wave
        /// </summary>
        static public float SineEaseInOut(float p)
        {
            return 0.5f * (1 - Math.Cos(p * PI));
        }

        /// <summary>
        /// Modeled after shifted quadrant IV of unit circle
        /// </summary>
        static public float CircularEaseIn(float p)
        {
            return 1 - Math.Sqrt(1 - (p * p));
        }

        /// <summary>
        /// Modeled after shifted quadrant II of unit circle
        /// </summary>
        static public float CircularEaseOut(float p)
        {
            return Math.Sqrt((2 - p) * p);
        }

        /// <summary>
        /// Modeled after the piecewise circular function
        /// y = (1/2)(1 - Math.Sqrt(1 - 4x^2))           ; [0, 0.5)
        /// y = (1/2)(Math.Sqrt(-(2x - 3)*(2x - 1)) + 1) ; [0.5, 1]
        /// </summary>
        static public float CircularEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 0.5f * (1 - Math.Sqrt(1 - 4 * (p * p)));
            }
            else
            {
                return 0.5f * (Math.Sqrt(-((2 * p) - 3) * ((2 * p) - 1)) + 1);
            }
        }

        /// <summary>
        /// Modeled after the exponential function y = 2^(10(x - 1))
        /// </summary>
        static public float ExponentialEaseIn(float p)
        {
            return (p == 0.0f) ? p : Math.Pow(2, 10 * (p - 1));
        }

        /// <summary>
        /// Modeled after the exponential function y = -2^(-10x) + 1
        /// </summary>
        static public float ExponentialEaseOut(float p)
        {
            return (p == 1.0f) ? p : 1 - Math.Pow(2, -10 * p);
        }

        /// <summary>
        /// Modeled after the piecewise exponential
        /// y = (1/2)2^(10(2x - 1))         ; [0,0.5)
        /// y = -(1/2)*2^(-10(2x - 1))) + 1 ; [0.5,1]
        /// </summary>
        static public float ExponentialEaseInOut(float p)
        {
            if (p == 0.0 || p == 1.0) return p;

            if (p < 0.5f)
            {
                return 0.5f * Math.Pow(2, (20 * p) - 10);
            }
            else
            {
                return -0.5f * Math.Pow(2, (-20 * p) + 10) + 1;
            }
        }

        /// <summary>
        /// Modeled after the damped sine wave y = sin(13pi/2*x)*Math.Pow(2, 10 * (x - 1))
        /// </summary>
        static public float ElasticEaseIn(float p)
        {
            return Math.Sin(13 * HALFPI * p) * Math.Pow(2, 10 * (p - 1));
        }

        /// <summary>
        /// Modeled after the damped sine wave y = sin(-13pi/2*(x + 1))*Math.Pow(2, -10x) + 1
        /// </summary>
        static public float ElasticEaseOut(float p)
        {
            return Math.Sin(-13 * HALFPI * (p + 1)) * Math.Pow(2, -10 * p) + 1;
        }

        /// <summary>
        /// Modeled after the piecewise exponentially-damped sine wave:
        /// y = (1/2)*sin(13pi/2*(2*x))*Math.Pow(2, 10 * ((2*x) - 1))      ; [0,0.5)
        /// y = (1/2)*(sin(-13pi/2*((2x-1)+1))*Math.Pow(2,-10(2*x-1)) + 2) ; [0.5, 1]
        /// </summary>
        static public float ElasticEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 0.5f * Math.Sin(13 * HALFPI * (2 * p)) * Math.Pow(2, 10 * ((2 * p) - 1));
            }
            else
            {
                return 0.5f * (Math.Sin(-13 * HALFPI * ((2 * p - 1) + 1)) * Math.Pow(2, -10 * (2 * p - 1)) + 2);
            }
        }

        /// <summary>
        /// Modeled after the overshooting cubic y = x^3-x*sin(x*pi)
        /// </summary>
        static public float BackEaseIn(float p)
        {
            return p * p * p - p * Math.Sin(p * PI);
        }

        /// <summary>
        /// Modeled after overshooting cubic y = 1-((1-x)^3-(1-x)*sin((1-x)*pi))
        /// </summary>
        static public float BackEaseOut(float p)
        {
            float f = (1 - p);
            return 1 - (f * f * f - f * Math.Sin(f * PI));
        }

        /// <summary>
        /// Modeled after the piecewise overshooting cubic function:
        /// y = (1/2)*((2x)^3-(2x)*sin(2*x*pi))           ; [0, 0.5)
        /// y = (1/2)*(1-((1-x)^3-(1-x)*sin((1-x)*pi))+1) ; [0.5, 1]
        /// </summary>
        static public float BackEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                float f = 2 * p;
                return 0.5f * (f * f * f - f * Math.Sin(f * PI));
            }
            else
            {
                float f = (1 - (2 * p - 1));
                return 0.5f * (1 - (f * f * f - f * Math.Sin(f * PI))) + 0.5f;
            }
        }

        /// <summary>
        /// </summary>
        static public float BounceEaseIn(float p)
        {
            return 1 - BounceEaseOut(1 - p);
        }

        /// <summary>
        /// </summary>
        static public float BounceEaseOut(float p)
        {
            if (p < 4 / 11.0f)
            {
                return (121 * p * p) / 16.0f;
            }
            else if (p < 8 / 11.0f)
            {
                return (363 / 40.0f * p * p) - (99 / 10.0f * p) + 17 / 5.0f;
            }
            else if (p < 9 / 10.0f)
            {
                return (4356 / 361.0f * p * p) - (35442 / 1805.0f * p) + 16061 / 1805.0f;
            }
            else
            {
                return (54 / 5.0f * p * p) - (513 / 25.0f * p) + 268 / 25.0f;
            }
        }

        /// <summary>
        /// </summary>
        static public float BounceEaseInOut(float p)
        {
            if (p < 0.5f)
            {
                return 0.5f * BounceEaseIn(p * 2);
            }
            else
            {
                return 0.5f * BounceEaseOut(p * 2 - 1) + 0.5f;
            }
        }
    }

}
