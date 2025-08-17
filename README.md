# FireNet
Realtime Multiplayer for Unity 3D using Firebase's Realtime-Database.
Copy of Pun 2 (without the closed source payment part)

# SETUP
1. Firebase Account with a Firebase App with Realtime-Database and Authentication (Email and Password auth option enabled)

HEAVILY RECOMMENDED:
2. Set your Realtime Database's rules to this:
{
  "rules": {
    ".read": "true",
    ".write": "auth != null"
  }
}

3. Firebase SDK installed in your unity project (find it here: https://github.com/firebase/firebase-unity-sdk/releases/latest) (Install Realtime Database, Authentication, and Optionally Analytics)

3. ENSURE there is a gameobject with the FireNetwork component in the scene!

4. Script whatever!!

# NOTICE!
If not obvious, I got lazy at points and used AI to run through the code and do stuff.

# WIP!!!
This is a WORK IN PROGRESS.
Has bugs.
Needs optimization.
Code isn't formatted very well, sorry lol.
Needs XML docs! Might just use one of those AI doc writers, im not rlly wanting to write docs :(
