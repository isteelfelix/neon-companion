package com.neoncompanion.filepicker;

import android.app.Activity;
import android.content.ContentResolver;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;

import com.unity3d.player.UnityPlayer;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.io.OutputStream;

public final class NeonFilePickerActivity extends Activity {
    private static final int RequestPickImage = 4017;
    private static final String ExtraGameObject = "unityGameObject";

    private String unityGameObject;
    private boolean resultSent;

    public static void pick(Activity activity, String unityGameObject) {
        Intent intent = new Intent(activity, NeonFilePickerActivity.class);
        intent.putExtra(ExtraGameObject, unityGameObject);
        activity.startActivity(intent);
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        unityGameObject = getIntent().getStringExtra(ExtraGameObject);

        Intent intent = new Intent(Intent.ACTION_GET_CONTENT);
        intent.setType("image/*");
        intent.addCategory(Intent.CATEGORY_OPENABLE);

        try {
            startActivityForResult(Intent.createChooser(intent, "Select avatar image"), RequestPickImage);
        } catch (Exception ignored) {
            sendResult("");
            finish();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        String path = "";
        if (requestCode == RequestPickImage && resultCode == RESULT_OK && data != null && data.getData() != null) {
            path = copyPickedImage(data.getData());
        }

        sendResult(path);
        finish();
    }

    @Override
    protected void onDestroy() {
        if (!resultSent) {
            sendResult("");
        }

        super.onDestroy();
    }

    private String copyPickedImage(Uri uri) {
        ContentResolver resolver = getContentResolver();
        String extension = extensionForMime(resolver.getType(uri));
        File directory = new File(getFilesDir(), "avatars/imports");
        if (!directory.exists() && !directory.mkdirs()) {
            return "";
        }

        File target = new File(directory, "avatar_" + System.currentTimeMillis() + extension);

        try (InputStream input = resolver.openInputStream(uri);
             OutputStream output = new FileOutputStream(target)) {
            if (input == null) {
                return "";
            }

            byte[] buffer = new byte[8192];
            int read;
            while ((read = input.read(buffer)) != -1) {
                output.write(buffer, 0, read);
            }

            return target.getAbsolutePath();
        } catch (Exception ignored) {
            return "";
        }
    }

    private static String extensionForMime(String mime) {
        if ("image/jpeg".equals(mime)) {
            return ".jpg";
        }

        if ("image/webp".equals(mime)) {
            return ".webp";
        }

        return ".png";
    }

    private void sendResult(String path) {
        if (resultSent) {
            return;
        }

        resultSent = true;
        if (unityGameObject != null && unityGameObject.length() > 0) {
            UnityPlayer.UnitySendMessage(unityGameObject, "OnAndroidImagePicked", path == null ? "" : path);
        }
    }
}
