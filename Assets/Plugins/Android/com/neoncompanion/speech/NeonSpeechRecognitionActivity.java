package com.neoncompanion.speech;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.speech.RecognizerIntent;
import java.util.ArrayList;
import com.unity3d.player.UnityPlayer;

public class NeonSpeechRecognitionActivity extends Activity {

    private static final int SPEECH_REQUEST_CODE = 4242;
    private static String bridgeGameObjectName;

    public static void startRecognition(Activity activity, String gameObjectName) {
        bridgeGameObjectName = gameObjectName;
        Intent intent = new Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH);
        intent.putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM);
        intent.putExtra(RecognizerIntent.EXTRA_PROMPT, "Speak now...");
        intent.putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1);

        try {
            activity.startActivityForResult(intent, SPEECH_REQUEST_CODE);
        } catch (Exception e) {
            // If no speech recognizer available, send empty result immediately
            UnityPlayer.UnitySendMessage(bridgeGameObjectName, "OnAndroidSpeechResult", "");
            if (activity instanceof NeonSpeechRecognitionActivity) {
                ((NeonSpeechRecognitionActivity) activity).finish();
            }
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        String resultText = "";
        if (requestCode == SPEECH_REQUEST_CODE) {
            if (resultCode == RESULT_OK && data != null) {
                ArrayList<String> results = data.getStringArrayListExtra(RecognizerIntent.EXTRA_RESULTS);
                if (results != null && !results.isEmpty()) {
                    resultText = results.get(0);
                }
            }
            // On cancel or error, resultText stays ""
            UnityPlayer.UnitySendMessage(bridgeGameObjectName, "OnAndroidSpeechResult", resultText);
        }

        finish();
    }
}
