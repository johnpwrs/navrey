// Print "<window id>\t<owner>\t<title>" for every on-screen window.
// Used by window_shot.sh; CGWindowList needs no accessibility permission.
import CoreGraphics
import Foundation

let opts = CGWindowListOption(arrayLiteral: .optionOnScreenOnly, .excludeDesktopElements)
guard let infos = CGWindowListCopyWindowInfo(opts, kCGNullWindowID) as? [[String: Any]] else {
    exit(1)
}
for w in infos {
    let owner = w[kCGWindowOwnerName as String] as? String ?? ""
    let name = w[kCGWindowName as String] as? String ?? ""
    let num = w[kCGWindowNumber as String] as? Int ?? -1
    print("\(num)\t\(owner)\t\(name)")
}
