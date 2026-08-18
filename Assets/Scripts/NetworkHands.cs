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

    private byte[] leftBytes = new byte[VectorHand.NUM_BYTES], 
                   rightBytes = new byte[VectorHand.NUM_BYTES];
    private bool leftTracked, rightTracked;

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
            leftModel.leapProvider = null;
            rightModel.leapProvider = null;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner)
            leapProvider.OnUpdateFrame -= OnUpdateFrame;
    }

    private void OnUpdateFrame(Frame frame)
    {
        int ind = frame.Hands.FindIndex(x => x.IsLeft);
        leftTracked = ind != -1;
        if (leftTracked) { leftVector.Encode(frame.Hands[ind]); leftVector.FillBytes(leftBytes); }

        ind = frame.Hands.FindIndex(x => !x.IsLeft);
        rightTracked = ind != -1;
        if (rightTracked) { rightVector.Encode(frame.Hands[ind]); rightVector.FillBytes(rightBytes); }

        UpdateHandServerRpc(NetworkManager.LocalClientId, leftTracked, rightTracked, leftBytes, rightBytes);
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
            if (lTracked) { leftVector.ReadBytes(lBytes); leftVector.Decode(leftHand); leftModel.SetLeapHand(leftHand); leftModel.UpdateHand(); }
        }
        if (rightModel != null)
        {
            rightModel.gameObject.SetActive(rTracked);
            if (rTracked) { rightVector.ReadBytes(rBytes); rightVector.Decode(rightHand); rightModel.SetLeapHand(rightHand); rightModel.UpdateHand(); }
        }
    }
}