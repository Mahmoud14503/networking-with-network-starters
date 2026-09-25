using Leap;
using Leap.Encoding;
using UnityEngine;
using Unity.Netcode;

public class NetworkHands : NetworkBehaviour
{
    [SerializeField]
    private HandModelBase leftModel = null, rightModel = null;

    private LeapProvider leapProvider;

    private VectorHand leftVector = new VectorHand(), rightVector = new VectorHand();
    private Hand leftHand = new Hand(), rightHand = new Hand();
    private Hand leftHandLatest = new Hand(), rightHandLatest = new Hand();

    private byte[] leftBytes = new byte[VectorHand.NUM_BYTES],
                   rightBytes = new byte[VectorHand.NUM_BYTES];
    private bool leftTracked, rightTracked;
    private bool leftHandReady, rightHandReady;

    private void Awake()
    {
        leapProvider = Hands.Provider;
    }

    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            // This is our local player — send our tracking data, 
            // destroy the remote hand visuals (we use Physical Hands locally)
            leapProvider.OnUpdateFrame += OnUpdateFrame;
            Destroy(leftModel?.gameObject);
            Destroy(rightModel?.gameObject);
        }
        else
        {
            // This is a remote player — drive their hands from network data,
            // not from a local LeapProvider
            if (leftModel != null) leftModel.leapProvider = null;
            if (rightModel != null) rightModel.leapProvider = null;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner && leapProvider != null)
            // We no longer need this event as we have disconnected from the network
            leapProvider.OnUpdateFrame -= OnUpdateFrame;
    }

    // Drive remote hands every frame from latest received data
    private void Update()
    {
        if (IsOwner) return;

        if (leftModel != null && leftHandReady)
        {
            leftModel.SetLeapHand(leftHandLatest);
            leftModel.UpdateHand();
        }

        if (rightModel != null && rightHandReady)
        {
            rightModel.SetLeapHand(rightHandLatest);
            rightModel.UpdateHand();
        }
    }

    private float _lastSendTime;
    private const float SEND_INTERVAL = 0.016f; // 60Hz

    private void OnUpdateFrame(Frame frame)
    {
        if (Time.time - _lastSendTime < SEND_INTERVAL) return;
        _lastSendTime = Time.time;

        int ind = frame.Hands.FindIndex(x => x.IsLeft);
        leftTracked = ind != -1;
        if (leftTracked) { leftVector.Encode(frame.Hands[ind]); leftVector.FillBytes(leftBytes); }

        ind = frame.Hands.FindIndex(x => !x.IsLeft);
        rightTracked = ind != -1;
        if (rightTracked) { rightVector.Encode(frame.Hands[ind]); rightVector.FillBytes(rightBytes); }

        UpdateHandServerRpc(NetworkManager.Singleton.LocalClientId, leftTracked, rightTracked, leftBytes, rightBytes);
    }

    [ServerRpc]
    private void UpdateHandServerRpc(ulong clientId, bool lTracked, bool rTracked, byte[] lBytes, byte[] rBytes)
    {
        LoadHandsData(lTracked, rTracked, lBytes, rBytes);
        UpdateHandClientRpc(clientId, lTracked, rTracked, lBytes, rBytes);
    }

    [ClientRpc]
    private void UpdateHandClientRpc(ulong clientId, bool lTracked, bool rTracked, byte[] lBytes, byte[] rBytes)
    {
        if (IsOwner) return;
        LoadHandsData(lTracked, rTracked, lBytes, rBytes);
    }

    private void LoadHandsData(bool lTracked, bool rTracked, byte[] lBytes, byte[] rBytes)
    {
        if (leftModel != null)
        {
            leftModel.gameObject.SetActive(lTracked);
            if (lTracked)
            {
                leftVector.ReadBytes(lBytes);
                leftVector.Decode(leftHandLatest); // store latest, Update() drives it
                leftHandReady = true;
            }
            else leftHandReady = false;
        }

        if (rightModel != null)
        {
            rightModel.gameObject.SetActive(rTracked);
            if (rTracked)
            {
                rightVector.ReadBytes(rBytes);
                rightVector.Decode(rightHandLatest);
                rightHandReady = true;
            }
            else rightHandReady = false;
        }
    }
}