using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A Hummingbird Machine Learning Agent
/// </summary>
public class HummingbirdAgent : Agent
{
    [Tooltip("Force to apply when moving")]
    public float moveForce = 2f;

    [Tooltip("Speed to pitch up or down")]
    public float pitchSpeed = 100f;

    [Tooltip("Speed to rotate around the up axis")]
    public float yawSpeed = 100f;

    [Tooltip("Tranform at the tip of the peak")]
    public Transform beakTip;

    [Tooltip("The agent's camera")]
    public Camera agentCamera;

    [Tooltip("Whether this is training mode or gameplay mode")]
    public bool trainingMode;

    // The rigidbody of t    agent
    new private Rigidbody rigidbody;

    // The flower area that the agent is in
    private FlowerArea flowerArea;

    // The nearest flower to the agent
    private Flower nearestFlower;

    // Allows for smoother pitch changes
    private float smoothPitchChange = 0f;

    // Allows for smoother yaw changes
    private float smoothYawChange = 0f;

    // Maximum angle that the bird can pitch up or down
    private const float MaxPitchAngle = 80f;

    // Maximum distance from the beak tip to accept nectar collision
    private const float BeakTipRadius = 0.008f;

    // whether the agent is frozen (intentionally not flying)
    private bool frozen = false;

    /// <summary>
    /// The amount of nectar the agent has obtained this episode
    /// </summary>
    public float NectarObtained { get; private set; }
    private float currentPitch = 0f;
    private float currentYaw = 0f;
    /// <summary>
    /// Initalize the agent
    /// </summary>
    public override void Initialize()
    {
        rigidbody = GetComponent<Rigidbody>();
        flowerArea = GetComponentInParent<FlowerArea>();

        // if not training mode, no max step, play forever
        if (!trainingMode) MaxStep = 0;

    }

    /// <summary>
    /// Reset the agent when an episode begins
    /// </summary>
    public override void OnEpisodeBegin()
    {
        if (trainingMode)
        {
            // only reset flowers in training when there is one agent per area
            flowerArea.ResetFlowers();
        }

        // Reset nectar obtained
        NectarObtained = 0f;

        // Zero out velocities so that movement stops before a new episode begins
        rigidbody.linearVelocity = Vector3.zero;
        rigidbody.angularVelocity = Vector3.zero;

        // Default to spawning in front of a flower
        bool inFrontOfFlower = true;
        if (trainingMode)
        {
            // spawn in front of flower 50% of the time during training

            inFrontOfFlower = UnityEngine.Random.value > .5f;

        }

        // Move the agent to a new random position
        MoveToSafeRandomPosition(inFrontOfFlower);

        // Recalculate the nearest flower now that the agent is moved
        UpdateNearestFlower();
    }

    /// <summary>
    /// Calledd when an action is recieved from either the player input or the neural network
    /// 
    /// actions.continuousActions[i] represents:
    /// Index 0: move vector X ( + 1 = right, -1 = left)
    /// Index 1: move vector Y ( + 1 = up, -1 = down)
    /// Index 2: move vector Z ( +1 = forward, -1 = backward)
    /// Index 3: pitch angle ( +1 = pitch up, -1 = pitch down)
    /// Index 4: yaw angle ( +1 = turn right, -1 = turn left)
    /// 
    /// </summary>
    /// <param name="actions">The actions to take</param>
    public override void OnActionReceived(ActionBuffers actions)
    {
        // Don't take actions if frozen
        if (frozen) return;

        // calculate movement vector
        Vector3 move = new Vector3(actions.ContinuousActions[0], actions.ContinuousActions[1], actions.ContinuousActions[2]);

        // Add force in the direction of the move vector
        rigidbody.AddForce(move * moveForce);

        // Get the current rotation
        Vector3 rotationVector = transform.rotation.eulerAngles;

        // calculate pitch and yaw rotation
        // 1. Get the action values (-1 to 1)
        float pitchChange = actions.ContinuousActions[3];
        float yawChange = actions.ContinuousActions[4];

        // 2. Smooth the input changes
        smoothPitchChange = Mathf.MoveTowards(smoothPitchChange, pitchChange, 2f * Time.fixedDeltaTime);
        smoothYawChange = Mathf.MoveTowards(smoothYawChange, yawChange, 2f * Time.fixedDeltaTime);

        // FIX: INCREMENT the current values instead of recalculating from a base vector
        currentPitch += smoothPitchChange * pitchSpeed * Time.fixedDeltaTime;
        currentYaw += smoothYawChange * yawSpeed * Time.fixedDeltaTime;

        // Clamp pitch to avoid flipping upside down
        currentPitch = Mathf.Clamp(currentPitch, -MaxPitchAngle, MaxPitchAngle);

        // Wrap yaw around 360 degrees to prevent overflow numbers
        if (currentYaw > 360f) currentYaw -= 360f;
        if (currentYaw < -360f) currentYaw += 360f;

        // 3. Apply the accumulated rotation
        transform.rotation = Quaternion.Euler(currentPitch, currentYaw, 0f);
    }

    /// <summary>
    /// Collect vector observations from the enviroment
    /// </summary>
    /// <param name="sensor">The vector sensor</param>
    public override void CollectObservations(VectorSensor sensor)
    {

        if (nearestFlower == null)
        {
            sensor.AddObservation(new float[10]);
            return;
        }
        // observe the agent's local rotation (4 observations)
        sensor.AddObservation(transform.localRotation.normalized);

        // Get a vector from the beak tip to the nearest flower
        Vector3 toFlower = nearestFlower.FlowerCenterPosition - beakTip.position;

        // observe a normalized vector pointing to the nearest flower (3 observations)
        sensor.AddObservation(toFlower.normalized);

        // observe a dot product that indicates whether the beak tip is in front of the flower (1 observation)
        // ( + 1 means that the beak tip is directly in front of the flowe, -1 means directly behind)
        sensor.AddObservation(Vector3.Dot(toFlower.normalized, -nearestFlower.FlowerUpVector.normalized));

        // observe a dot product that indicates whether the beak is pointing towards a flower (1 observation)
        // ( +1 means that the beak is pointing directly at the flower, -1 means directly away)
        sensor.AddObservation(Vector3.Dot(beakTip.forward.normalized, -nearestFlower.FlowerUpVector.normalized));

        // observe the relative distance from the beak tip to the flower (1 observation) 
        sensor.AddObservation(toFlower.magnitude / FlowerArea.AreaDiameter);

        // 10 total observations
    }


    /// <summary>
    /// When behavior type is set to "Heuristic Only" on the agent's behavior parameters,
    /// This function will be called. Its retunrs values will fed into
    /// <see cref="OnActionReceived(ActionBuffers)"/> instead of using the neural network
    /// </summary>
    /// <param name="actionsOut">and output action arrays</param>
    public override void Heuristic(in ActionBuffers actionsOut)
    {
        ActionSegment<float> continuousActions = actionsOut.ContinuousActions;
        var keyboard = Keyboard.current;

        // Safety check in case no keyboard is connected
        if (keyboard == null) return;

        Vector3 forward = Vector3.zero;
        Vector3 left = Vector3.zero;
        Vector3 up = Vector3.zero;
        float pitch = 0f;
        float yaw = 0f;

        // Forward/backward (W/S)
        if (keyboard.wKey.isPressed) forward = transform.forward;
        else if (keyboard.sKey.isPressed) forward = -transform.forward;

        // Left/Right (A/D)
        if (keyboard.aKey.isPressed) left = -transform.right;
        else if (keyboard.dKey.isPressed) left = transform.right;

        // Up/down (E/C)
        if (keyboard.eKey.isPressed) up = transform.up;
        else if (keyboard.cKey.isPressed) up = -transform.up;

        // Pitch up/down (Arrow keys)
        if (keyboard.upArrowKey.isPressed) pitch = 1f;
        else if (keyboard.downArrowKey.isPressed) pitch = -1f;

        // Turn left/right (Arrow keys)
        if (keyboard.leftArrowKey.isPressed) yaw = -1f;
        else if (keyboard.rightArrowKey.isPressed) yaw = 1f;

        // Combine the movement vectors and normalize
        Vector3 combined = (forward + left + up).normalized;

        // Pass values to ML-Agents action buffer
        continuousActions[0] = combined.x;
        continuousActions[1] = combined.y;
        continuousActions[2] = combined.z;
        continuousActions[3] = pitch;
        continuousActions[4] = yaw;
    }

    /// <summary>
    /// Prevent the agent from moving
    /// </summary>
    public void FreezeAgent()
    {
        Debug.Assert(trainingMode == false, "freeze/unfreeze not supported in training");
        frozen = true;
        rigidbody.Sleep();

    }

    /// <summary>
    /// Resume agent movement and actions
    /// </summary>
    public void UnfreezeAgent()
    {
        Debug.Assert(trainingMode == false, "freeze/unfreeze not supported in training");
        frozen = false;
        rigidbody.WakeUp();

    }

    /// <summary>
    /// Move the agent to a safe random postion (i.e does not collide with anything)
    /// If in front of flower, also point at the flower
    /// </summary>
    /// <param name="inFrontOfFlower">Whether to choose a spot in front of a flower</param>
    private void MoveToSafeRandomPosition(bool inFrontOfFlower)
    {
        bool safePositionFound = false;
        int attemptsRemaining = 100; // prevents an inifinte loop
        Vector3 potentialPosition = Vector3.zero;

        Quaternion potentialRotation = new Quaternion();

        // loop until a safe postion is found or we run out of attempts
        while (!safePositionFound && attemptsRemaining > 0)
        {
            attemptsRemaining--;
            if (inFrontOfFlower)
            {
                // pick a random flower
                Flower randomFlower = flowerArea.Flowers[UnityEngine.Random.Range(0, flowerArea.Flowers.Count)];

                // position 10 to 20 cm in front of flower
                float distanceFromFlower = UnityEngine.Random.Range(.1f, .2f);
                potentialPosition = randomFlower.transform.position + randomFlower.FlowerUpVector * distanceFromFlower;

                // point beak at flower (bird's head is center of transform)
                Vector3 toFlower = randomFlower.FlowerCenterPosition - potentialPosition;
                potentialRotation = Quaternion.LookRotation(toFlower, Vector3.up);

            }
            else
            {
                // pick random height from the ground
                float height = UnityEngine.Random.Range(1.2f, 2.5f);

                // pick a random radius from the center of the area
                float radius = UnityEngine.Random.Range(2f, 7f);

                // pick a random direction rotated around the y-axis  
                Quaternion direction = Quaternion.Euler(0f, UnityEngine.Random.Range(-189f, 180f), 0f);

                // combine height, radius, and direction to pick a potential position

                potentialPosition = flowerArea.transform.position + Vector3.up * height + direction * Vector3.forward * radius;

                // choose and set random starting pitch and yaw
                float pitch = UnityEngine.Random.Range(-60f, 60f);
                float yaw = UnityEngine.Random.Range(-180f, 180f);
                potentialRotation = Quaternion.Euler(-pitch, yaw, 0f);
            }

            // check to see if the agent will collide with anything
            Collider[] colliders = Physics.OverlapSphere(potentialPosition, 0.05f);

            // safe position has been found if no colliders are overlapped
            safePositionFound = colliders.Length == 0;
        }

        Debug.Assert(safePositionFound, "Could not find a safe position to spawn");

        // set the position and rotation
        transform.position = potentialPosition;
        transform.rotation = potentialRotation;
    }



    /// <summary>
    /// Update the nearest flower to the agent
    /// </summary>
    private void UpdateNearestFlower()
    {
        foreach (Flower flower in flowerArea.Flowers)
        {
            if (nearestFlower == null && flower.HasNectar)
            {
                // no current nearest flower and this flower has nectar, so set to this flower
                nearestFlower = flower;
            }
            else if (flower.HasNectar)
            {
                // calculate distance to this flower and distance to the current nearest flower
                float distanceToFlower = Vector3.Distance(flower.transform.position, beakTip.position);
                float distanceToCurrentNearestFlower = Vector3.Distance(nearestFlower.transform.position, beakTip.position);

                // if current nearest flower is empty OR this flower is closer, update the nearestFlower
                if (!nearestFlower.HasNectar || distanceToFlower < distanceToCurrentNearestFlower)
                {
                    nearestFlower = flower;
                }
            }
        }
    }

    /// <summary>
    /// Called when the agent's collider enters a trigger collider
    /// </summary>
    /// <param name="other">The trigger collider</param>
    private void OnTriggerEnter(Collider other)
    {
        TriggerEnterOrStay(other);
    }

    /// <summary>
    /// Called when the agent's collider stays a trigger collider
    /// </summary>
    /// <param name="other">The trigger collider</param>
    private void OnTriggerStay(Collider other)
    {
        TriggerEnterOrStay(other);
    }

    /// <summary>
    /// Handles when the agent's collider enters of stays in a trigger collider
    /// </summary>
    /// <param name="collider">The trigger collider</param>
    private void TriggerEnterOrStay(Collider collider)
    {
        // check if agents is colliding with nectar
        if (collider.CompareTag("nectar"))
        {
            Vector3 closetPointToBeakTip = collider.ClosestPoint(beakTip.position);

            // check if the closet collision point is close to the beak tip
            // Note: a collision with anything but the beak tip should not count
            if (Vector3.Distance(beakTip.position, closetPointToBeakTip) < BeakTipRadius)
            {
                // Look up the flower for this nectar collider
                Flower flower = flowerArea.GetFlowerFromNectar(collider);

                // Attempt to take .01 nectar
                // Note: This is per fixed timestep, meaning it happens every .02 s or 50x per second

                float nectarReceived = flower.Feed(.01f);
                NectarObtained += nectarReceived;

                if (trainingMode)
                {
                    // calculate reward for getting nectar
                    float bonus = .02f * Mathf.Clamp01(Vector3.Dot(transform.forward.normalized, -nearestFlower.FlowerUpVector.normalized));
                    AddReward(.01f + bonus);

                }

                // if the flower is empty, update the nearest flower
                if (!flower.HasNectar)
                {
                    UpdateNearestFlower();
                }
            }
        }
    }

    /// <summary>
    /// Called when agent collides with somethng solid
    /// </summary>
    /// <param name="collision">The collision info</param>
    private void OnCollisionEnter(Collision collision)
    {
        if (trainingMode && collision.collider.CompareTag("boundry"))
        {
            // Collided with the area boundry, give a negative reward
            AddReward(-.5f);
        }
    }


    /// <summary>
    /// Called every frame
    /// </summary>
    private void Update()
    {
        // Draw a line from the beak tip to the nearest flower
        if (nearestFlower != null)
        {
            Debug.DrawLine(beakTip.position, nearestFlower.FlowerCenterPosition, Color.green);

        }
    }

    /// <summary>
    /// Called every .02s
    /// </summary>
    private void FixedUpdate()
    {
        // Avoids a scenerio when another player steals the nectar and it doesnt update the nearest flower with nectar
        if (nearestFlower != null && !nearestFlower.HasNectar)
        {
            UpdateNearestFlower();
        }
    }
}
