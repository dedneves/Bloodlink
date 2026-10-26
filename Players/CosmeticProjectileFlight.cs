using UnityEngine;

namespace ULTRAKILL.Bloodlink.Players
{
    internal sealed class CosmeticProjectileFlight : MonoBehaviour
    {
        private Vector3 velocity;
        private Vector3 gravity;
        private Vector3 angularVelocity;
        private float remaining = 5f;

        public void Initialize(Vector3 initialVelocity, Vector3 gravityAcceleration, Vector3 initialAngularVelocity, float lifetime = 5f)
        {
            velocity = initialVelocity;
            gravity = gravityAcceleration;
            angularVelocity = initialAngularVelocity;
            remaining = Mathf.Clamp(lifetime, 0.1f, 10f);
        }

        private void Update()
        {
            var delta = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            transform.position += velocity * delta;
            velocity += gravity * delta;
            transform.Rotate(angularVelocity * Mathf.Rad2Deg * delta, Space.World);
            remaining -= delta;
            if (remaining <= 0f) Destroy(gameObject);
        }
    }
}
