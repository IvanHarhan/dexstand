using Fusion;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Main menu and pause screen. The launcher no longer starts a session on its
/// own, so nothing happens until a mode is chosen here.
/// </summary>
public class GameMenu : MonoBehaviour
{
    [Header("Panels")]
    [SerializeField] private GameObject mainPanel;
    [SerializeField] private GameObject pausePanel;
    [SerializeField] private GameObject hud;

    [Header("Scene")]
    [SerializeField] private NetworkLauncher launcher;

    [Tooltip("Touch controls and crosshair — hidden while a menu is up.")]
    [SerializeField] private GameObject gameplayUI;

    public bool IsPaused { get; private set; }
    public bool InMenu { get; private set; } = true;

    private void Start()
    {
        ShowMain();
    }

    private void Update()
    {
        // Esc toggles pause once a match is running.
        if (!InMenu && UnityEngine.InputSystem.Keyboard.current != null &&
            UnityEngine.InputSystem.Keyboard.current.escapeKey.wasPressedThisFrame)
        {
            if (IsPaused) Resume();
            else Pause();
        }
    }

    // ---------------------------------------------------------------- main menu

    public void ShowMain()
    {
        InMenu = true;
        IsPaused = false;
        Time.timeScale = 1f;

        Set(mainPanel, true);
        Set(pausePanel, false);
        Set(gameplayUI, false);
        Set(hud, false);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void StartSinglePlayer()
    {
        // A per-device room name keeps solo players out of each other's games.
        Begin(GameMode.Single, $"solo-{SystemInfo.deviceUniqueIdentifier}");
    }

    public void StartMultiplayer()
    {
        Begin(GameMode.AutoHostOrClient, "room1");
    }

    private void Begin(GameMode mode, string session)
    {
        if (launcher == null)
        {
            Debug.LogError("[GameMenu] No launcher assigned.");
            return;
        }

        InMenu = false;
        Set(mainPanel, false);
        Set(gameplayUI, true);
        Set(hud, true);

        launcher.StartSession(mode, session);
    }

    public void Quit()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    // -------------------------------------------------------------------- pause

    public void Pause()
    {
        if (InMenu) return;

        IsPaused = true;
        Time.timeScale = 0f;
        Set(pausePanel, true);
        Set(gameplayUI, false);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    public void Resume()
    {
        IsPaused = false;
        Time.timeScale = 1f;
        Set(pausePanel, false);
        Set(gameplayUI, true);
    }

    /// <summary>
    /// Leaving a match tears the runner down and reloads the scene: spawned
    /// players and targets carry networked state that is not worth unwinding
    /// by hand.
    /// </summary>
    public void LeaveToMenu()
    {
        Time.timeScale = 1f;
        if (launcher != null) launcher.StopSession();
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    private static void Set(GameObject go, bool active)
    {
        if (go != null) go.SetActive(active);
    }
}
