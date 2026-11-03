"use strict";

// Online play (accounts, the player list and live two-player matches) runs on Firebase.
// Until this is filled in, the rest of the game works normally and "Online battle" explains the setup.
//
// 1. Create a project at https://console.firebase.google.com (the free Spark plan is enough).
// 2. Build → Authentication → Get started → enable "Email/Password".
// 3. Build → Firestore Database → Create database (production mode), then paste firestore.rules into its Rules tab.
// 4. Authentication → Settings → Authorized domains → add your GitHub Pages domain (e.g. mr-chigozie.github.io).
// 5. Project settings → Your apps → Web app (</>) → copy the config object over the null below.
//
// These values identify the project; they are not secret. Access is protected by firestore.rules.
const FIREBASE_CONFIG = null;
/* Example:
const FIREBASE_CONFIG = {
  apiKey: "AIza...",
  authDomain: "your-project.firebaseapp.com",
  projectId: "your-project",
  storageBucket: "your-project.appspot.com",
  messagingSenderId: "1234567890",
  appId: "1:1234567890:web:abc123"
};
*/
