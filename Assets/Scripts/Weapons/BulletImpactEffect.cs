using UnityEngine;

namespace Scorched.Weapons
{
    /// <summary>
    /// High-impact ballistic hit effect for bullet projectiles.
    /// Spawns dynamic ricochet sparks, an incandescent flash light, and debris.
    /// Auto-cleans itself from memory after emission completes.
    /// </summary>
    [DisallowMultipleComponent]
    public class BulletImpactEffect : MonoBehaviour
    {
        [Header("Impact Timing")]
        [SerializeField] private float _lifetime = 1.2f;

        private void Start()
        {
            CreateSparksIfMissing();
            CreatePointLightFlash();
            Destroy(gameObject, _lifetime);
        }

        private void CreatePointLightFlash()
        {
            var lightGo = new GameObject("ImpactFlash");
            lightGo.transform.SetParent(transform, false);
            lightGo.transform.localPosition = Vector3.forward * 0.05f;

            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = new Color(1.0f, 0.75f, 0.3f);
            light.intensity = 4.0f;
            light.range = 3.5f;
            light.shadows = LightShadows.None;

            // Fast decay
            StartCoroutine(FadeLight(light, 0.08f));
        }

        private System.Collections.IEnumerator FadeLight(Light light, float duration)
        {
            float elapsed = 0f;
            float initialIntensity = light.intensity;

            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                if (light != null)
                {
                    light.intensity = Mathf.Lerp(initialIntensity, 0f, elapsed / duration);
                }
                yield return null;
            }

            if (light != null)
            {
                light.enabled = false;
            }
        }

        private void CreateSparksIfMissing()
        {
            var ps = GetComponent<ParticleSystem>();
            if (ps != null) return;

            ps = gameObject.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.duration = 0.25f;
            main.loop = false;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 18f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.02f, 0.06f);
            main.startColor = new Color(1.0f, 0.75f, 0.25f, 1.0f);
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1.5f;

            var emission = ps.emission;
            emission.rateOverTime = 0;
            emission.SetBursts(new ParticleSystem.Burst[] { new ParticleSystem.Burst(0f, 18, 30) });

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 35f;
            shape.radius = 0.03f;

            var colorOverLifetime = ps.colorOverLifetime;
            colorOverLifetime.enabled = true;
            Gradient grad = new Gradient();
            grad.SetKeys(
                new GradientColorKey[] {
                    new GradientColorKey(new Color(1f, 0.95f, 0.7f), 0f),
                    new GradientColorKey(new Color(1f, 0.4f, 0.05f), 0.6f),
                    new GradientColorKey(new Color(0.3f, 0.1f, 0.05f), 1f)
                },
                new GradientAlphaKey[] {
                    new GradientAlphaKey(1f, 0f),
                    new GradientAlphaKey(0.8f, 0.5f),
                    new GradientAlphaKey(0f, 1f)
                }
            );
            colorOverLifetime.color = grad;

            var sizeOverLifetime = ps.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            AnimationCurve curve = new AnimationCurve();
            curve.AddKey(0f, 1f);
            curve.AddKey(1f, 0.1f);
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, curve);

            var renderer = ps.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.cameraVelocityScale = 0f;
            renderer.velocityScale = 0.08f;
            renderer.lengthScale = 2.0f;

            // Ensure valid URP particle material (anti-pink)
            Material sparkMat = null;
#if UNITY_EDITOR
            sparkMat = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/M_BulletImpactSparks.mat");
#endif
            if (sparkMat == null)
            {
                Shader s = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                        ?? Shader.Find("Particles/Standard Unlit")
                        ?? Shader.Find("Sprites/Default");
                sparkMat = new Material(s);
                sparkMat.SetFloat("_Surface", 1.0f);
                sparkMat.SetFloat("_Blend", 1.0f);
                sparkMat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                sparkMat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.One);
                sparkMat.SetFloat("_ZWrite", 0.0f);
                sparkMat.SetFloat("_Cull", 0.0f);
                sparkMat.SetColor("_BaseColor", new Color(1.0f, 0.85f, 0.45f, 1.0f));
                sparkMat.SetColor("_EmissionColor", new Color(3.5f, 1.8f, 0.4f, 1.0f));
            }
            renderer.sharedMaterial = sparkMat;

            // Spawn persistent bullet hole mark
            if (BulletVisualPoolManager.Instance != null)
            {
                BulletVisualPoolManager.Instance.SpawnBulletMark(transform.position, transform.forward);
            }

            ps.Play();
        }
    }
}
