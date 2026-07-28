package com.neoncompanion.filepicker;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.provider.OpenableColumns;
import android.database.Cursor;
import com.unity3d.player.UnityPlayer;

public class NeonFilePickerActivity extends Activity {

    private static final int PICK_FILE_REQUEST = 1001;
    private static String bridgeGameObjectName;

    public static void pick(Activity activity, String gameObjectName) {
        bridgeGameObjectName = gameObjectName;
        // Launch OUR activity, not a chooser from the Unity activity — otherwise the result
        // is delivered to UnityPlayerActivity (which ignores it) and onActivityResult below
        // never runs, so the C# callback never fires (no attachment; second tap blocked).
        Intent intent = new Intent(activity, NeonFilePickerActivity.class);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity.startActivity(intent);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        Intent intent = new Intent(Intent.ACTION_GET_CONTENT);
        intent.setType("*/*");
        intent.addCategory(Intent.CATEGORY_OPENABLE);

        startActivityForResult(Intent.createChooser(intent, "Select file"), PICK_FILE_REQUEST);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode == PICK_FILE_REQUEST && resultCode == RESULT_OK && data != null) {
            Uri uri = data.getData();
            if (uri != null) {
                String path = getPathFromUri(uri);
                UnityPlayer.UnitySendMessage(bridgeGameObjectName, "OnAndroidImagePicked", path != null ? path : "");
            } else {
                UnityPlayer.UnitySendMessage(bridgeGameObjectName, "OnAndroidImagePicked", "");
            }
        } else {
            UnityPlayer.UnitySendMessage(bridgeGameObjectName, "OnAndroidImagePicked", "");
        }

        finish();
    }

    private String getPathFromUri(Uri uri) {
        // For content:// URIs, copy to persistentDataPath or return the URI string.
        // For simplicity in companion app, return the URI string - C# side can handle loading.
        // Better: copy file to app's cache for direct path access.
        try {
            String fileName = getFileName(uri);
            java.io.File cacheDir = getCacheDir();
            java.io.File outFile = new java.io.File(cacheDir, fileName != null ? fileName : "picked_file");

            java.io.InputStream input = getContentResolver().openInputStream(uri);
            java.io.OutputStream output = new java.io.FileOutputStream(outFile);

            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = input.read(buffer)) != -1) {
                output.write(buffer, 0, bytesRead);
            }
            input.close();
            output.close();

            return outFile.getAbsolutePath();
        } catch (Exception e) {
            e.printStackTrace();
            return uri.toString(); // fallback to uri
        }
    }

    private String getFileName(Uri uri) {
        String result = null;
        if ("content".equals(uri.getScheme())) {
            try (Cursor cursor = getContentResolver().query(uri, null, null, null, null)) {
                if (cursor != null && cursor.moveToFirst()) {
                    int nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                    if (nameIndex >= 0) {
                        result = cursor.getString(nameIndex);
                    }
                }
            }
        }
        if (result == null) {
            result = uri.getPath();
            int cut = result.lastIndexOf('/');
            if (cut != -1) result = result.substring(cut + 1);
        }
        return result;
    }
}
