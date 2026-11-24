// Google AdMob, kept out of the fighting:
//   - a banner along the bottom of the menus and the War Room, never over a battle;
//   - a full-screen ad between battles: only after a battle has finished, at most once every two finished battles
//     and four minutes apart, never in the middle of one (online games included);
//   - a rewarded ad, ready for a reward the game offers (nothing uses it yet).
// The consent form (EEA/UK, via Google's User Messaging Platform) runs before any ad is requested, and the
// title screen shows a "Privacy choices" button where the law asks for one.
//
// Going live: create the app and its three ad units in AdMob, put the IDs below and set Live = true. Development
// builds keep Google's test ads whatever Live says (clicking real ads in testing breaks AdMob's rules).
// The app ID is written into the Android manifest by BuildTools.BuildAndroid.
using System;
using GoogleMobileAds.Api;
using GoogleMobileAds.Ump.Api;
using UnityEngine;

namespace ChessWars.Game
{
    public static class Ads
    {
        // ---------------------------------------------------------------- IDs
        public const bool Live = false;
        const string LiveAppId = "ca-app-pub-0000000000000000~0000000000";
        const string LiveBanner = "ca-app-pub-0000000000000000/0000000000";
        const string LiveInterstitial = "ca-app-pub-0000000000000000/0000000000";
        const string LiveRewarded = "ca-app-pub-0000000000000000/0000000000";
        // Google's public test IDs (https://developers.google.com/admob/android/test-ads).
        const string TestAppId = "ca-app-pub-3940256099942544~3347511713";
        const string TestBanner = "ca-app-pub-3940256099942544/9214589741";
        const string TestInterstitial = "ca-app-pub-3940256099942544/1033173712";
        const string TestRewarded = "ca-app-pub-3940256099942544/5224354917";

        public static string AndroidAppId => Live ? LiveAppId : TestAppId;
        static bool UseLive => Live && !Debug.isDebugBuild;
        static string BannerId => UseLive ? LiveBanner : TestBanner;
        static string InterstitialId => UseLive ? LiveInterstitial : TestInterstitial;
        static string RewardedId => UseLive ? LiveRewarded : TestRewarded;

        // ---------------------------------------------------------------- pacing
        const int BattlesPerBreak = 2;
        const float MinSecondsBetween = 240f;

        /// <summary>Bought "Remove ads" (the store hook): no banner and no breaks. Rewarded ads stay, as they're asked for.</summary>
        public static bool Removed
        {
            get => PlayerPrefs.GetInt("cw.noads", 0) == 1;
            set { PlayerPrefs.SetInt("cw.noads", value ? 1 : 0); PlayerPrefs.Save(); if (value) Banner(false); }
        }

        /// <summary>The banner's height in screen pixels while it shows (0 otherwise): menus keep their buttons above it.</summary>
        public static float BannerPixels => bannerShown && bannerLoaded && banner != null ? banner.GetHeightInPixels() : 0f;

        /// <summary>The law asks for a way back to the consent choices here (EEA/UK).</summary>
        public static bool PrivacyOptionsRequired
        {
            get
            {
                // Asked of the SDK at most once a second (the title screen checks every frame).
                if (started && Time.realtimeSinceStartup >= privacyCheckedAt + 1f)
                {
                    privacyCheckedAt = Time.realtimeSinceStartup;
                    privacyRequired = ConsentInformation.PrivacyOptionsRequirementStatus == PrivacyOptionsRequirementStatus.Required;
                }
                return privacyRequired;
            }
        }
        static bool privacyRequired;
        static float privacyCheckedAt = -9f;

        static bool started, ready, wantBanner, bannerShown, bannerLoaded, showing;
        static BannerView banner;
        static InterstitialAd interstitial;
        static RewardedAd rewarded;
        static int battlesSinceBreak;
        static float lastBreakAt = -9999f;

        static bool Supported => Application.platform == RuntimePlatform.Android;

        /// <summary>Ask for consent where needed, then start the ads SDK. Safe to call more than once.</summary>
        public static void Init()
        {
            if (started || !Supported) return;
            started = true;
            MobileAds.RaiseAdEventsOnUnityMainThread = true;
            // Testing (development builds): -e adbreak 1 starts with a full-screen ad due, without two battles first.
            if (Debug.isDebugBuild && ChessWars.Online.Server.IntentExtra("adbreak") == "1") { battlesSinceBreak = BattlesPerBreak; lastBreakAt = -9999f; }
            Debug.Log("[Ads] checking consent");
            consentAskedAt = Time.realtimeSinceStartup;
            // The consent callbacks come on a Java thread: hop back to the game's thread before going on.
            ConsentInformation.Update(new ConsentRequestParameters(), updateError =>
            {
                Debug.Log($"[Ads] consent info back{(updateError != null ? ": " + updateError.Message : "")}");
                OnMain(() => ConsentForm.LoadAndShowConsentFormIfRequired(formError =>
                {
                    Debug.Log($"[Ads] consent form done{(formError != null ? ": " + formError.Message : "")}");
                    consentAskedAt = -1f;
                    OnMain(StartSdk);
                }));
            });
        }

        // If the consent reply never comes back (it can go astray on some devices), go on after a while with the
        // consent Google has stored: CanRequestAds() still says no where consent is needed and wasn't given.
        const float ConsentPatience = 8f;
        static float consentAskedAt = -1f;
        static bool sdkStarting;

        static readonly System.Collections.Concurrent.ConcurrentQueue<Action> mainQueue = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        static void OnMain(Action a) => mainQueue.Enqueue(a);

        // Run what the SDK callbacks queued (called every frame, from Banner).
        static void Pump()
        {
            if (consentAskedAt >= 0 && Time.realtimeSinceStartup - consentAskedAt > ConsentPatience)
            {
                consentAskedAt = -1f;
                if (!sdkStarting) { Debug.Log("[Ads] no consent reply yet; going on with the stored consent"); StartSdk(); }
            }
            while (mainQueue.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        static void StartSdk()
        {
            bool can = ConsentInformation.CanRequestAds();
            Debug.Log($"[Ads] consent done, can request ads: {can}");
            if (sdkStarting || ready || !can) return;
            sdkStarting = true;
            // Battles, not gore: keep ads to a teen rating.
            MobileAds.SetRequestConfiguration(new RequestConfiguration { MaxAdContentRating = MaxAdContentRating.T });
            MobileAds.Initialize(_ => OnMain(() =>
            {
                ready = true;
                Debug.Log("[Ads] ready");
                LoadInterstitial();
                LoadRewarded();
                if (wantBanner) Banner(true);
            }));
        }

        /// <summary>The privacy choices form (the button the title screen shows when PrivacyOptionsRequired).</summary>
        public static void ShowPrivacyOptions()
        {
            if (!started) return;
            ConsentForm.ShowPrivacyOptionsForm(error =>
            {
                if (error != null) Debug.LogWarning($"[Ads] privacy options: {error.Message}");
                OnMain(StartSdk);   // a "yes" given here lets ads start
            });
        }

        // ---------------------------------------------------------------- banner

        /// <summary>Show or hide the bottom banner. Called every frame by the game with "is a menu showing".</summary>
        public static void Banner(bool show)
        {
            Pump();
            wantBanner = show && !Removed;
            if (!ready || showing) return;
            if (wantBanner && banner == null)
            {
                var size = AdSize.GetCurrentOrientationAnchoredAdaptiveBannerAdSizeWithWidth(AdSize.FullWidth);
                banner = new BannerView(BannerId, size, AdPosition.Bottom);
                banner.OnBannerAdLoaded += () => bannerLoaded = true;
                banner.OnBannerAdLoadFailed += e => Debug.LogWarning($"[Ads] banner: {e.GetMessage()}");
                banner.LoadAd(new AdRequest());
                bannerShown = true;
                return;
            }
            if (banner == null || wantBanner == bannerShown) return;
            if (wantBanner) banner.Show();
            else banner.Hide();
            bannerShown = wantBanner;
        }

        // ---------------------------------------------------------------- between battles

        /// <summary>A battle reached its end (checkmate, draw, resignation, time).</summary>
        public static void BattleFinished() => battlesSinceBreak++;

        /// <summary>Leaving a finished battle: show the full-screen ad if one is due, then carry on.</summary>
        public static void Break(Action then)
        {
            bool due = ready && !Removed && !showing && battlesSinceBreak >= BattlesPerBreak
                && Time.realtimeSinceStartup - lastBreakAt >= MinSecondsBetween
                && interstitial != null && interstitial.CanShowAd();
            if (!due) { then?.Invoke(); return; }
            var ad = interstitial;
            interstitial = null;
            battlesSinceBreak = 0;
            lastBreakAt = Time.realtimeSinceStartup;
            FullScreen(h => ad.OnAdFullScreenContentClosed += h, h => ad.OnAdFullScreenContentFailed += _ => h(), () =>
            {
                ad.Destroy();
                LoadInterstitial();
                then?.Invoke();
            });
            ad.Show();
        }

        static void LoadInterstitial()
        {
            if (!ready || interstitial != null) return;
            InterstitialAd.Load(InterstitialId, new AdRequest(), (ad, error) =>
            {
                if (error != null || ad == null) { Debug.LogWarning($"[Ads] interstitial: {error?.GetMessage()}"); return; }
                interstitial = ad;
            });
        }

        // ---------------------------------------------------------------- rewarded

        public static bool RewardedReady => ready && rewarded != null && rewarded.CanShowAd();

        /// <summary>Play a rewarded ad; `done(true)` once it has been watched to the reward.</summary>
        public static void Rewarded(Action<bool> done)
        {
            if (!RewardedReady || showing) { done?.Invoke(false); return; }
            var ad = rewarded;
            rewarded = null;
            bool earned = false;
            FullScreen(h => ad.OnAdFullScreenContentClosed += h, h => ad.OnAdFullScreenContentFailed += _ => h(), () =>
            {
                ad.Destroy();
                LoadRewarded();
                done?.Invoke(earned);
            });
            ad.Show(_ => earned = true);
        }

        static void LoadRewarded()
        {
            if (!ready || rewarded != null) return;
            RewardedAd.Load(RewardedId, new AdRequest(), (ad, error) =>
            {
                if (error != null || ad == null) { Debug.LogWarning($"[Ads] rewarded: {error?.GetMessage()}"); return; }
                rewarded = ad;
            });
        }

        // While a full-screen ad plays: the banner hides, the game's sound stops, and `after` runs once it closes or fails.
        static void FullScreen(Action<Action> onClosed, Action<Action> onFailed, Action after)
        {
            showing = true;
            if (banner != null && bannerShown) { banner.Hide(); bannerShown = false; }
            AudioListener.pause = true;
            bool done = false;
            void Finish()
            {
                if (done) return;
                done = true;
                showing = false;
                AudioListener.pause = false;
                after();
            }
            onClosed(Finish);
            onFailed(Finish);
        }
    }
}
