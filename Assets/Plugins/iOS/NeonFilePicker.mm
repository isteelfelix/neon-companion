//
//  NeonFilePicker.mm
//  iOS native file/image picker bridge (UIDocumentPickerViewController / PHPicker)
//
//  See docs/17_iOS_Platform_Architecture.md (IOS-02)
//
//  Usage from C#:
//  NeonFilePicker_PickImage("iOSFilePickerBridge");
//  NeonFilePicker_PickFile("iOSFilePickerBridge", ".txt");
//
//  On selection: copies file to persistentDataPath and calls
//  UnitySendMessage(goName, "OnFilePicked", fullPath);
//

#import <Foundation/Foundation.h>
#import <UIKit/UIKit.h>
#import <PhotosUI/PhotosUI.h>

extern "C" {

    void NeonFilePicker_PickImage(const char* gameObjectName) {
        NSString *goName = [NSString stringWithUTF8String:gameObjectName ?: @""];
        NSLog(@"[NeonFilePicker] PickImage requested for GameObject: %@", goName);

        // Placeholder implementation
        // Real version:
        // - Get top UIViewController: UnityGetGLViewController() or [UIApplication sharedApplication].keyWindow.rootViewController
        // - Present PHPickerViewController (iOS 14+) or UIImagePickerController for images
        // - On pick: copy to [NSSearchPathForDirectoriesInDomains(NSDocumentDirectory, NSUserDomainMask, YES) firstObject]
        // - Call UnitySendMessage([goName UTF8String], "OnFilePicked", [path UTF8String]);

        // For build compatibility we simulate a "no selection" so C# fallback triggers
        // UnitySendMessage([goName UTF8String], "OnFilePicked", "");
    }

    void NeonFilePicker_PickFile(const char* gameObjectName, const char* extension) {
        NSString *goName = [NSString stringWithUTF8String:gameObjectName ?: @""];
        NSString *ext = [NSString stringWithUTF8String:extension ?: @""];
        NSLog(@"[NeonFilePicker] PickFile requested (ext: %@) for %@", ext, goName);

        // Similar: present UIDocumentPickerViewController with UTType for the extension
        // UnitySendMessage on completion with copied path
    }

} // extern "C"
