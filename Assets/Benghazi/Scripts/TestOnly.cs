using UnityEngine;

// On something that exists only for the automated play-test (the steel practice targets): it is switched off in
// a normal game and stays only when the player is started with -autotest, which shoots at them.
public class TestOnly : MonoBehaviour
{
    void Awake()
    {
        if (!Application.isEditor && !System.Environment.CommandLine.Contains("-autotest")) gameObject.SetActive(false);
    }
}
