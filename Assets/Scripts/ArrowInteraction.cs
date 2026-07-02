using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class ArrowInteraction : MonoBehaviour
{
    [Header("Physics Fallback Settings")]
    public string handTag = "PlayerHand";
    public string handLayer = "VR_Hands";

    [Header("Visual Effects")]
    public ParticleSystem destructionParticles;

    private bool _isDestroying = false;

    private void OnTriggerEnter(Collider other)
    {
        bool isHand = other.CompareTag(handTag) || other.gameObject.layer == LayerMask.NameToLayer(handLayer);
        if (isHand) Disintegrate();
    }

    public void Disintegrate()
    {
        if (_isDestroying) return;
        _isDestroying = true;

        Debug.Log($"[ArrowInteraction] Disintegrate called on '{gameObject.name}' | " +
                  $"active={gameObject.activeInHierarchy} | instanceID={gameObject.GetInstanceID()}");

        GetComponent<Collider>().enabled = false;

        var renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        Debug.Log($"[ArrowInteraction] Found {renderers.Length} Renderer(s) on '{gameObject.name}':");
        foreach (var r in renderers)
            Debug.Log($"  - {r.GetType().Name} on '{r.gameObject.name}' | enabled={r.enabled} | visible={r.isVisible}");

        if (destructionParticles != null)
        {
            ParticleSystem ash = Instantiate(destructionParticles, transform.position, transform.rotation);
            var main = ash.main;
            if (renderers.Length > 0 && renderers[0] is MeshRenderer mr)
                main.startColor = mr.material.color;
            float lifetime = main.duration + main.startLifetime.constantMax;
            Destroy(ash.gameObject, lifetime);
            Debug.Log($"[ArrowInteraction] Particles spawned, will self-destroy in {lifetime:F1}s");
        }
        else
        {
            Debug.LogWarning($"[ArrowInteraction] destructionParticles is NULL on '{gameObject.name}'");
        }

        foreach (var r in renderers)
            r.enabled = false;

        Debug.Log($"[ArrowInteraction] All renderers disabled. Scheduling Destroy in 1 frame.");
        StartCoroutine(DestroyNextFrame());
    }

    private IEnumerator DestroyNextFrame()
    {
        yield return null;
        Debug.Log($"[ArrowInteraction] Destroy executing on '{gameObject.name}' | " +
                  $"still exists={this != null}");
        Destroy(gameObject);
    }
}