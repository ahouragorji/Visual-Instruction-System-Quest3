using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class ArrowInteraction : MonoBehaviour
{
    [Header("Physics Fallback Settings")]
    public string handTag = "PlayerHand";
    public string handLayer = "VR_Hands";

    [Header("NextStep")]


    [Header("Visual Effects")]
    public ParticleSystem destructionParticles;

    private bool _isDestroying = false;

    // 1. THIS LISTENS TO DUMB PHYSICS (Poking / Bumping)
    private void OnTriggerEnter(Collider other)
    {
        bool isHand = other.CompareTag(handTag) || other.gameObject.layer == LayerMask.NameToLayer(handLayer);
        
        if (isHand)
        {
            Disintegrate(); // Trigger the exact same explosion
        }
    }

    // 2. THIS LISTENS TO THE INTERACTION SDK (Pinching / Gripping)
    public void Disintegrate()
    {

        
        if (_isDestroying) return;
        _isDestroying = true;

        //questInstructionReceiver.AdvanceToNextStep();
        // Turn off the collider so it can't be touched twice
        GetComponent<Collider>().enabled = false;

        MeshRenderer meshRenderer = GetComponentInChildren<MeshRenderer>();

        if (destructionParticles != null)
        {
            ParticleSystem ash = Instantiate(destructionParticles, transform.position, transform.rotation);
            
            if (meshRenderer != null)
            {
                var main = ash.main;
                main.startColor = meshRenderer.material.color; 
            }
        }

        if (meshRenderer != null)
        {
            meshRenderer.enabled = false;
        }

        Destroy(gameObject);
    }
}